using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Council;
using Axiom.Core.Memory;
using Axiom.Core.Persistence;
using Axiom.Core.Tools;
using Axiom.Core.Workspace;

namespace Axiom.Cli.Ui;

// Self-contained chat interface: alternate-screen full window with
//   [ header ]
//   [ scrollable message viewport  ← app-managed, not host scrollbar ]
//   [ activity line ]
//   [ fixed prompt box + model line ]
//
// Scroll: ↑↓ (empty input) · Shift+↑↓ · PgUp/PgDn · Ctrl+↑↓ · mouse wheel.
// Transcript is app-managed (alt-screen); prompt stays pinned.
internal sealed class ChatTui : IDisposable
{
    private enum Role { User, Assistant, System, Status, Error }

    private sealed class Msg
    {
        public required Role Role { get; init; }
        public required string Text { get; set; }
    }

    private readonly TerminalScreen _screen = new();
    private readonly List<Msg> _messages = new();
    private readonly object _gate = new();
    private readonly SessionStore _sessionStore = new();

    private string _input = string.Empty;
    private int _cursor;
    private int _scrollFromBottom; // 0 = follow latest
    private int _menuIndex;
    private string _activity = string.Empty;
    private bool _busy;
    private int _animFrame;
    private string[] _animFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private int _turnCount;
    private bool _running = true;
    private DateTime _lastPaint = DateTime.MinValue;
    private string _sessionId = SessionStore.NewId();
    private DateTime _sessionCreated = DateTime.UtcNow;
    private CancellationTokenSource? _turnCts;
    private readonly StringBuilder _streamBuffer = new();
    private int _streamAssistantIndex = -1;
    // Council roles may report from parallel worker tasks. The terminal itself is single-threaded,
    // so workers enqueue events and the input/render loop applies them in order.
    private readonly ConcurrentQueue<CouncilEvent> _pendingCouncilEvents = new();
    private TaskCompletionSource<bool>? _approvalTcs;
    private string _approvalPrompt = string.Empty;
    private TaskCompletionSource<string>? _criticPickTcs;
    private string _criticPickBuffer = string.Empty;
    private string _criticPickPrompt = string.Empty;

    // Workflow UX (#1)
    private bool _paletteOpen;
    private string _paletteQuery = string.Empty;
    private int _paletteIndex;
    private bool _sessionPickerOpen;
    private int _sessionPickerIndex;
    private string? _sessionTitle; // custom name from /rename
    private string? _lastUserTask; // for /continue
    private string _profileName = "default";
    private Axiom.Core.Persistence.UserProfileStore? _profiles;
    private Axiom.Core.Persistence.UserProfile? _profile;

    // Session bindings
    private ChatSession? _session;
    private (string Id, string Label, string Description)[] _models = Array.Empty<(string, string, string)>();
    private Func<string, ChatSession, Task>? _handleSlash;
    private Func<string, ChatSession, Task<string>>? _augmentTools;
    private Action<string, ChatSession>? _attachPaths;

    public void Dispose() => _screen.Dispose();

    public void Notify(string message) => PushSystem(message);

    public void ClearTranscript()
    {
        lock (_gate)
        {
            _messages.Clear();
            _scrollFromBottom = 0;
        }
        // New blank session id so the next auto-save doesn't overwrite the previous file.
        _sessionId = SessionStore.NewId();
        _sessionCreated = DateTime.UtcNow;
        _sessionTitle = null;
        _lastUserTask = null;
    }

    public void SetProfile(string name, Axiom.Core.Persistence.UserProfileStore store, Axiom.Core.Persistence.UserProfile profile)
    {
        _profileName = name;
        _profiles = store;
        _profile = profile;
    }

    public string? LastUserTask => _lastUserTask;

    public bool TryRenameCurrentSession(string title, out string error)
    {
        error = string.Empty;
        title = (title ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length == 0)
        {
            error = "Title is empty. Usage: /rename my topic";
            return false;
        }
        if (title.Length > 80)
            title = title[..77] + "...";
        _sessionTitle = title;
        if (!_sessionStore.TryRename(_sessionId, title, out error))
        {
            // Session file may not exist yet — still keep in-memory title for next save.
            error = string.Empty;
        }
        PushSystem($"Session renamed → {_sessionTitle}");
        AutoSave();
        return true;
    }

    public string ExportTranscript(bool lastTurnOnly)
    {
        List<Msg> snap;
        lock (_gate) snap = _messages.ToList();
        if (lastTurnOnly)
        {
            int lastUser = snap.FindLastIndex(m => m.Role == Role.User);
            if (lastUser >= 0)
                snap = snap.Skip(lastUser).ToList();
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Axiom session export");
        sb.AppendLine();
        sb.AppendLine($"- Id: `{_sessionId}`");
        sb.AppendLine($"- Title: {_sessionTitle ?? "(auto)"}");
        sb.AppendLine($"- Model: {_session?.ModelLabel}");
        sb.AppendLine($"- Exported: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (Msg m in snap)
        {
            string role = m.Role switch
            {
                Role.User => "You",
                Role.Assistant => "Axiom",
                Role.Error => "Error",
                Role.Status => "Status",
                _ => "System"
            };
            sb.AppendLine($"## {role}");
            sb.AppendLine();
            sb.AppendLine(m.Text);
            sb.AppendLine();
        }

        string dir = System.IO.Path.Combine(Axiom.Core.AppPaths.Root, "Exports");
        System.IO.Directory.CreateDirectory(dir);
        string file = System.IO.Path.Combine(dir,
            $"axiom-{_sessionId}-{(lastTurnOnly ? "last" : "full")}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        System.IO.File.WriteAllText(file, sb.ToString());
        PushSystem($"Exported → {file}");
        return file;
    }

    public async Task ContinueLastTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastUserTask))
        {
            PushSystem("Nothing to continue — no prior user task in this session.");
            return;
        }
        if (_busy)
        {
            PushSystem("Already busy — wait or Esc to stop.");
            return;
        }
        PushSystem("Continuing last task…");
        await SubmitAsync(_lastUserTask + "\n\n[CONTINUE] Resume and finish this task. Use workspace state and prior progress; do not restart from scratch unless necessary.");
    }

    public void CycleApprovalMode()
    {
        if (_session == null)
            return;
        _session.Tools.ApprovalMode = _session.Tools.ApprovalMode switch
        {
            ApprovalMode.Auto => ApprovalMode.Ask,
            ApprovalMode.Ask => ApprovalMode.Plan,
            _ => ApprovalMode.Auto
        };
        _session.ApplyToolSettings();
        PersistProfileFromSession();
        PushSystem($"Approval mode → {_session.Tools.ApprovalLabel}  (Ctrl+Shift+M to cycle)");
    }

    public void OpenSessionPicker()
    {
        _sessionPickerOpen = true;
        _sessionPickerIndex = 0;
        _paletteOpen = false;
    }

    public void OpenCommandPalette()
    {
        _paletteOpen = true;
        _paletteQuery = string.Empty;
        _paletteIndex = 0;
        _sessionPickerOpen = false;
    }

    public bool TryLoadSession(string idOrPrefix, out string error)
    {
        error = string.Empty;
        StoredSession? stored = _sessionStore.Load(idOrPrefix);
        if (stored == null)
        {
            error = $"Session not found: {idOrPrefix}";
            return false;
        }

        if (_session == null)
        {
            error = "No active session.";
            return false;
        }

        lock (_gate)
        {
            _messages.Clear();
            foreach (StoredUiMessage m in stored.Messages ?? [])
            {
                Role role = m.Role?.ToLowerInvariant() switch
                {
                    "user" => Role.User,
                    "assistant" => Role.Assistant,
                    "status" => Role.Status,
                    "error" => Role.Error,
                    _ => Role.System
                };
                _messages.Add(new Msg { Role = role, Text = m.Text ?? string.Empty });
            }
            _scrollFromBottom = 0;
        }

        _sessionId = stored.Id;
        _sessionCreated = stored.CreatedAt == default ? DateTime.UtcNow : stored.CreatedAt;
        _sessionTitle = string.IsNullOrWhiteSpace(stored.Title) || stored.Title.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase)
            ? null
            : stored.Title;
        if (!string.IsNullOrWhiteSpace(stored.ModelId))
            _session.ModelId = stored.ModelId;
        if (!string.IsNullOrWhiteSpace(stored.ModelLabel))
            _session.ModelLabel = stored.ModelLabel;

        _session.History.Clear();
        foreach (OpenRouterMessageDto h in stored.History ?? [])
            _session.History.Add(new OpenRouterMessage(h.Role ?? "user", h.Text ?? string.Empty));

        if (stored.WorkspaceRoots is { Count: > 0 })
            _session.Workspace.SetRoots(stored.WorkspaceRoots, stored.WorkspaceExclusive);

        return true;
    }

    public IReadOnlyList<SessionListItem> ListSessions() => _sessionStore.List();

    public string CurrentSessionId => _sessionId;

    public bool DeleteSession(string idOrPrefix) => _sessionStore.Delete(idOrPrefix);

    /// <summary>Delete the current saved session file and start a blank chat (same window).</summary>
    public bool DeleteCurrentAndStartFresh()
    {
        bool deleted = _sessionStore.Delete(_sessionId);
        ClearTranscript();
        if (_session != null)
            _session.History.Clear();
        PushSystem(deleted
            ? "Session deleted. Fresh chat started."
            : "No saved file for this chat (or already gone). Fresh chat started.");
        return deleted;
    }

    public int DeleteAllSessionsAndStartFresh()
    {
        int n = _sessionStore.DeleteAll();
        ClearTranscript();
        if (_session != null)
            _session.History.Clear();
        PushSystem(n == 0
            ? "No saved sessions to delete. Fresh chat started."
            : $"Deleted {n} saved session(s). Fresh chat started.");
        return n;
    }

    public async Task<int> RunAsync(
        ChatSession session,
        (string Id, string Label, string Description)[] models,
        Func<string, ChatSession, Task> handleSlash,
        Func<string, ChatSession, Task<string>> augmentTools,
        Action<string, ChatSession> attachPaths,
        Func<string, bool>? saveApiKey = null)
    {
        _session = session;
        _models = models;
        _handleSlash = handleSlash;
        _augmentTools = augmentTools;
        _attachPaths = attachPaths;

        _screen.Enter();

        // First-run: popup to paste OpenRouter API key (Enter to submit). Skipped entirely when
        // a custom endpoint is already configured -- a custom-endpoint-only user has no
        // OpenRouter key to enter and shouldn't be trapped behind this modal.
        if (!session.ChatService.HasValidKey && !session.ChatService.HasValidCustomEndpoint)
        {
            string? key = await PromptApiKeyModalAsync();
            if (string.IsNullOrWhiteSpace(key))
            {
                _screen.Leave();
                return 1;
            }

            if (saveApiKey != null && !saveApiKey(key))
            {
                PushError("Could not save API key to local database.");
            }
            session.ChatService.SetApiKey(key.Trim());
            PushSystem("OpenRouter API key saved.");
            MarkOnboarding("api_key");
        }
        else
        {
            MarkOnboarding("api_key");
        }

        // Resume last session if present (Claude Code-style continuity).
        TryAutoResumeLastSession();

        string mode = session.Tools.ApprovalLabel;
        bool isCustomEndpointModel = string.Equals(session.ModelId, OpenRouterChatService.CustomEndpointModelId, StringComparison.OrdinalIgnoreCase);
        string councilLabel = session.Tools.CouncilEnabled
            ? (isCustomEndpointModel ? "on (lite)" : "on")
            : "off";
        PushSystem(
            $"Axiom ready · {session.ModelLabel} · council {councilLabel} · mode {mode} · profile @{_profileName}");
        PushSystem("Ctrl+K palette · Ctrl+Shift+M mode · Esc stop · /continue · /export · /sessions");

        var memFiles = ProjectMemory.ListLoadedFiles(session.Workspace.PrimaryRoot);
        if (memFiles.Count > 0)
            PushSystem("Project memory: " + string.Join(", ", memFiles));

        ShowOnboardingIfNeeded();

        // Wire approval prompts for Ask mode.
        session.ToolExecutor.ApprovalHandler = PromptToolApprovalAsync;

        using var animCts = new CancellationTokenSource();
        var animTask = AnimateLoopAsync(animCts.Token);

        try
        {
            Paint();
            while (_running)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(16);
                    if (Console.WindowWidth != _screen.Width || Console.WindowHeight != _screen.Height)
                        Paint(force: true);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                // Approve/deny modal takes Esc/y/n while waiting.
                if (_approvalTcs != null)
                {
                    if (key.Key is ConsoleKey.Y || key.KeyChar is 'y' or 'Y')
                    {
                        _approvalTcs.TrySetResult(true);
                        _approvalTcs = null;
                        _approvalPrompt = string.Empty;
                        Paint(force: true);
                        continue;
                    }
                    if (key.Key is ConsoleKey.N or ConsoleKey.Escape || key.KeyChar is 'n' or 'N')
                    {
                        _approvalTcs.TrySetResult(false);
                        _approvalTcs = null;
                        _approvalPrompt = string.Empty;
                        Paint(force: true);
                        continue;
                    }
                }

                // Critic user-in-loop: type all | none | 1,3 then Enter
                if (_criticPickTcs != null)
                {
                    if (key.Key == ConsoleKey.Enter)
                    {
                        _criticPickTcs.TrySetResult(_criticPickBuffer.Trim());
                        _criticPickTcs = null;
                        _criticPickBuffer = string.Empty;
                        _criticPickPrompt = string.Empty;
                        Paint(force: true);
                        continue;
                    }
                    if (key.Key == ConsoleKey.Escape)
                    {
                        _criticPickTcs.TrySetResult("all");
                        _criticPickTcs = null;
                        _criticPickBuffer = string.Empty;
                        _criticPickPrompt = string.Empty;
                        Paint(force: true);
                        continue;
                    }
                    if (key.Key == ConsoleKey.Backspace && _criticPickBuffer.Length > 0)
                    {
                        _criticPickBuffer = _criticPickBuffer[..^1];
                        SetActivity(_criticPickPrompt + _criticPickBuffer);
                        Paint(force: true);
                        continue;
                    }
                    if (!char.IsControl(key.KeyChar))
                    {
                        _criticPickBuffer += key.KeyChar;
                        SetActivity(_criticPickPrompt + _criticPickBuffer);
                        Paint(force: true);
                        continue;
                    }
                    continue;
                }

                // Esc stops the in-flight turn (Claude Code / Codex style).
                if (_busy && key.Key == ConsoleKey.Escape)
                {
                    _turnCts?.Cancel();
                    SetActivity("Stopping…");
                    Paint(force: true);
                    continue;
                }

                if (await HandleKeyAsync(key))
                    Paint();
            }
        }
        finally
        {
            animCts.Cancel();
            try { await animTask; } catch { /* ignore */ }
            _screen.Leave();
        }

        return 0;
    }

    // Centered modal: paste OpenRouter API key, Enter submits, Esc cancels.
    private async Task<string?> PromptApiKeyModalAsync()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            PaintApiKeyModal(buffer.ToString());
            while (!Console.KeyAvailable)
                await Task.Delay(16);

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                string value = buffer.ToString().Trim();
                if (value.Length > 10)
                    return value;
                // Too short — keep prompting.
                continue;
            }
            if (key.Key == ConsoleKey.Escape)
                return null;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Remove(buffer.Length - 1, 1);
                continue;
            }
            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                buffer.Append(key.KeyChar);
        }
    }

    private void PaintApiKeyModal(string typed)
    {
        _screen.RefreshSize();
        int w = _screen.Width;
        int h = _screen.Height;
        var rows = new string[h];
        for (int i = 0; i < h; i++)
            rows[i] = Ansi.Fg(AxiomTheme.Background) + new string(' ', w) + Ansi.Reset;

        string title = "OpenRouter API key";
        string hint = "Paste your key from openrouter.ai/keys  ·  Enter save  ·  Esc quit";
        string masked = typed.Length == 0
            ? "sk-or-v1-…"
            : new string('•', Math.Min(typed.Length, Math.Max(12, w / 3)));

        int boxW = Math.Min(w - 6, 64);
        int boxH = 9;
        int top = Math.Max(1, (h - boxH) / 2);
        int left = Math.Max(2, (w - boxW) / 2);

        // Draw box rows as full-width strings for reliability.
        string border = Ansi.Fg(AxiomTheme.Border);
        string gold = Ansi.Fg(AxiomTheme.Gold);
        string muted = Ansi.Fg(AxiomTheme.SystemMuted);
        string primary = Ansi.Fg(AxiomTheme.TextPrimary);

        for (int r = 0; r < h; r++)
            rows[r] = new string(' ', w);

        rows[top] = PadCenter(w, "╭" + new string('─', boxW - 2) + "╮", border);
        rows[top + 1] = PadCenter(w, "│" + CenterIn(boxW - 2, title) + "│", gold + Ansi.Bold);
        rows[top + 2] = PadCenter(w, "│" + new string(' ', boxW - 2) + "│", border);
        rows[top + 3] = PadCenter(w, "│" + CenterIn(boxW - 2, "Paste key, then press Enter") + "│", muted);
        rows[top + 4] = PadCenter(w, "│" + new string(' ', boxW - 2) + "│", border);
        // Input field line
        string field = "  " + (typed.Length == 0 ? masked : (typed.Length <= boxW - 6 ? typed : "…" + typed[^(boxW - 7)..]));
        if (field.Length > boxW - 2) field = field[..(boxW - 2)];
        field = field.PadRight(boxW - 2);
        rows[top + 5] = PadCenter(w, "│" + field + "│", primary);
        rows[top + 6] = PadCenter(w, "│" + new string(' ', boxW - 2) + "│", border);
        rows[top + 7] = PadCenter(w, "│" + CenterIn(boxW - 2, "Enter · submit     Esc · quit") + "│", muted);
        rows[top + 8] = PadCenter(w, "╰" + new string('─', boxW - 2) + "╯", border);
        rows[Math.Min(h - 1, top + 10)] = PadCenter(w, hint, muted);

        _ = left;
        _screen.Paint(rows);
        // Cursor inside the field (ShowCursorAt is 1-based).
        int col = Math.Min(w, (w - boxW) / 2 + 3 + Math.Min(typed.Length, boxW - 8));
        _screen.ShowCursorAt(top + 6, Math.Max(1, col)); // row: top(0-based)+5 content → +6 in 1-based for field line top+5 → 1-based top+6
    }

    private static string CenterIn(int width, string text)
    {
        if (text.Length >= width) return text[..width];
        int pad = (width - text.Length) / 2;
        return new string(' ', pad) + text + new string(' ', width - pad - text.Length);
    }

    private static string PadCenter(int width, string content, string colorPrefix)
    {
        // content includes box characters already at natural width; center the whole content string
        if (content.Length >= width)
            return colorPrefix + content[..width] + Ansi.Reset;
        int pad = (width - content.Length) / 2;
        return colorPrefix + new string(' ', pad) + content + new string(' ', width - pad - content.Length) + Ansi.Reset;
    }

    private async Task AnimateLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(90, token); }
            catch (OperationCanceledException) { break; }

            bool hadWorkflowEvents = DrainWorkflowNotifications();

            if (_busy)
            {
                Interlocked.Increment(ref _animFrame);
                Paint();
            }
            else if (hadWorkflowEvents)
            {
                Paint(force: true);
            }
        }
    }

    private async Task<bool> HandleKeyAsync(ConsoleKeyInfo key)
    {
        MenuMode mode = GetMenuMode(_input, _cursor);

        // Ctrl+K — command palette
        if (!_busy && key.Key == ConsoleKey.K && key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && !key.Modifiers.HasFlag(ConsoleModifiers.Shift)
            && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            OpenCommandPalette();
            return true;
        }

        // Ctrl+Shift+M — cycle approval mode
        if (!_busy && key.Key == ConsoleKey.M
            && key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && key.Modifiers.HasFlag(ConsoleModifiers.Shift))
        {
            CycleApprovalMode();
            return true;
        }

        if (_paletteOpen)
            return await HandlePaletteKeyAsync(key);

        if (_sessionPickerOpen)
            return await HandleSessionPickerKeyAsync(key);

        // Mouse wheel / incomplete ESC sequences (SGR mouse) when mouse tracking is on.
        if (TryConsumeMouseWheel(key, out int wheelDelta))
        {
            ScrollBy(wheelDelta);
            return true;
        }

        // Transcript scroll — works while idle, typing (Shift), or generating.
        if (TryHandleScrollKey(key, mode))
            return true;

        if (_busy)
            return false; // ignore typing while generating (scroll still handled above)

        // Slash / @ menu navigation (only when not scrolling)
        if (mode != MenuMode.None && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            var items = GetMenuItems(mode);
            if (items.Count > 0)
            {
                _menuIndex = key.Key == ConsoleKey.UpArrow
                    ? (_menuIndex - 1 + items.Count) % items.Count
                    : (_menuIndex + 1) % items.Count;
                return true;
            }
        }

        if (key.Key == ConsoleKey.Enter)
        {
            if (mode != MenuMode.None)
            {
                var items = GetMenuItems(mode);
                if (items.Count > 0)
                {
                    string? autoSubmit = ApplyMenuSelection(items[Math.Clamp(_menuIndex, 0, items.Count - 1)], mode);
                    if (autoSubmit != null)
                    {
                        _input = string.Empty;
                        _cursor = 0;
                        _menuIndex = 0;
                        await SubmitAsync(autoSubmit);
                    }
                    return true;
                }
            }

            string submitted = _input;
            _input = string.Empty;
            _cursor = 0;
            _menuIndex = 0;
            await SubmitAsync(submitted);
            return true;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            if (mode == MenuMode.Slash)
            {
                _input = string.Empty;
                _cursor = 0;
            }
            else if (mode == MenuMode.At)
            {
                RemoveAtToken();
            }
            return true;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_cursor > 0)
            {
                _input = _input.Remove(_cursor - 1, 1);
                _cursor--;
                _menuIndex = 0;
            }
            return true;
        }

        if (key.Key == ConsoleKey.Delete)
        {
            if (_cursor < _input.Length)
            {
                _input = _input.Remove(_cursor, 1);
                _menuIndex = 0;
            }
            return true;
        }

        if (key.Key == ConsoleKey.LeftArrow)
        {
            if (_cursor > 0) _cursor--;
            return true;
        }
        if (key.Key == ConsoleKey.RightArrow)
        {
            if (_cursor < _input.Length) _cursor++;
            return true;
        }
        if (key.Key == ConsoleKey.Home)
        {
            _cursor = 0;
            return true;
        }
        if (key.Key == ConsoleKey.End)
        {
            _cursor = _input.Length;
            return true;
        }

        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            _input = _input.Insert(_cursor, key.KeyChar.ToString());
            _cursor++;
            _menuIndex = 0;
            return true;
        }

        return false;
    }

    private bool TryHandleScrollKey(ConsoleKeyInfo key, MenuMode mode)
    {
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        bool alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        bool emptyInput = string.IsNullOrEmpty(_input);
        // Plain ↑↓ scroll when not in a menu and the input box is empty (or Shift/Alt always).
        bool arrowScrollOk = mode == MenuMode.None && (emptyInput || shift || alt);

        int lineStep = 1;
        int blockStep = Math.Max(3, ViewportHeight() / 3);
        int pageStep = Math.Max(5, ViewportHeight() * 2 / 3);

        if (key.Key == ConsoleKey.PageUp
            || (key.Key == ConsoleKey.UpArrow && ctrl)
            || (key.Key == ConsoleKey.UpArrow && arrowScrollOk))
        {
            int step = key.Key == ConsoleKey.PageUp || ctrl ? pageStep
                : shift || alt ? blockStep
                : lineStep;
            // Empty-input plain arrow: use a comfortable block so history is usable.
            if (key.Key == ConsoleKey.UpArrow && emptyInput && !shift && !alt && !ctrl)
                step = blockStep;
            ScrollBy(step);
            return true;
        }

        if (key.Key == ConsoleKey.PageDown
            || (key.Key == ConsoleKey.DownArrow && ctrl)
            || (key.Key == ConsoleKey.DownArrow && arrowScrollOk))
        {
            int step = key.Key == ConsoleKey.PageDown || ctrl ? pageStep
                : shift || alt ? blockStep
                : lineStep;
            if (key.Key == ConsoleKey.DownArrow && emptyInput && !shift && !alt && !ctrl)
                step = blockStep;
            ScrollBy(-step);
            return true;
        }

        if (key.Key == ConsoleKey.Home && ctrl)
        {
            _scrollFromBottom = MaxScroll();
            return true;
        }

        if (key.Key == ConsoleKey.End && ctrl)
        {
            _scrollFromBottom = 0;
            return true;
        }

        return false;
    }

    private void ScrollBy(int deltaLines)
    {
        if (deltaLines == 0)
            return;
        int max = MaxScroll();
        _scrollFromBottom = Math.Clamp(_scrollFromBottom + deltaLines, 0, max);
    }

    // SGR mouse wheel: ESC [ < 64 ; col ; row M  (up) / 65 (down). ReadKey often delivers
    // Escape first; drain the rest of the sequence when available.
    private bool TryConsumeMouseWheel(ConsoleKeyInfo key, out int delta)
    {
        delta = 0;
        if (key.Key != ConsoleKey.Escape && key.KeyChar != '\u001b')
            return false;

        // Peek/build sequence from buffered keys (non-blocking).
        var sb = new StringBuilder();
        sb.Append('\u001b');
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!Console.KeyAvailable)
            {
                System.Threading.Thread.Sleep(1);
                continue;
            }
            ConsoleKeyInfo next = Console.ReadKey(intercept: true);
            sb.Append(next.KeyChar == '\0' ? string.Empty : next.KeyChar.ToString());
            // Also append if it's a letter/number from CSI
            if (next.KeyChar is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or 'M' or 'm' or '~')
                break;
            if (sb.Length > 64)
                break;
        }

        string seq = sb.ToString();
        // SGR wheel: \x1b[<64;…M or \x1b[<65;…M  (sometimes button+32 etc.)
        if (seq.Contains("<64;", StringComparison.Ordinal) || seq.Contains("<64M", StringComparison.Ordinal))
        {
            delta = Math.Max(3, ViewportHeight() / 4);
            return true;
        }
        if (seq.Contains("<65;", StringComparison.Ordinal) || seq.Contains("<65M", StringComparison.Ordinal))
        {
            delta = -Math.Max(3, ViewportHeight() / 4);
            return true;
        }
        // Legacy X10 mouse: \x1b[M Cb Cx Cy  where wheel up Cb=64, down=65 (with space offset: 96/97)
        if (seq.Length >= 6 && seq.StartsWith("\u001b[M", StringComparison.Ordinal))
        {
            int cb = seq[3];
            if (cb is 64 or 96) { delta = Math.Max(3, ViewportHeight() / 4); return true; }
            if (cb is 65 or 97) { delta = -Math.Max(3, ViewportHeight() / 4); return true; }
        }

        // Not a wheel event — if we ate a lone Escape meant for menu cancel, treat as Esc.
        if (seq is "\u001b" or "\u001b\u001b")
        {
            // Fake Esc handling path: return false and let Esc handler… but we already consumed.
            // Manually clear menus.
            if (!_busy)
            {
                MenuMode mode = GetMenuMode(_input, _cursor);
                if (mode == MenuMode.Slash)
                {
                    _input = string.Empty;
                    _cursor = 0;
                }
                else if (mode == MenuMode.At)
                {
                    RemoveAtToken();
                }
            }
            return true; // consumed Esc
        }

        return false;
    }

    private async Task SubmitAsync(string input)
    {
        if (_session == null)
            return;

        if (string.IsNullOrWhiteSpace(input))
            return;

        if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            _running = false;
            return;
        }

        if (input.StartsWith('/'))
        {
            // Let Program handle slash side-effects via callback; also mirror feedback in TUI.
            if (_handleSlash != null)
                await _handleSlash(input, _session);
            // Re-paint chrome after model/tool toggles
            Paint(force: true);
            return;
        }

        PushUser(input);
        _lastUserTask = input;
        MarkOnboarding("first_message");
        _attachPaths?.Invoke(input, _session);
        if (_session.Workspace.IsExclusive || _session.Workspace.Roots.Count > 0)
            MarkOnboarding("folder");
        _scrollFromBottom = 0;
        Paint(force: true);

        _busy = true;
        _turnCts?.Dispose();
        _turnCts = new CancellationTokenSource();
        CancellationToken turnToken = _turnCts.Token;
        SetActivity(ActivityStatus.Thinking);
        int assistantIndex = -1;
        var sw = Stopwatch.StartNew();
        int toolCalls = 0;

        try
        {
            string grounded = _augmentTools != null
                ? await _augmentTools(input, _session)
                : input;

            if (_session.Tools.CouncilEnabled)
            {
                await RunCouncilTurnAsync(grounded, sw, turnToken);
            }
            else
            {
                AgentLoop agent = _session.CreateAgent();
                var collected = new StringBuilder();

                Task<AgentTurnResult> agentTask = agent.RunAsync(
                    grounded,
                    _session.History,
                    onToken: token =>
                    {
                        collected.Append(token);
                        lock (_gate)
                        {
                            if (assistantIndex < 0)
                            {
                                _messages.Add(new Msg { Role = Role.Assistant, Text = collected.ToString() });
                                assistantIndex = _messages.Count - 1;
                            }
                            else
                            {
                                _messages[assistantIndex].Text = collected.ToString();
                            }
                        }
                        SetActivity(ActivityStatus.Generating);
                        ThrottledPaint();
                    },
                    onStatus: status =>
                    {
                        SetActivity(status);
                        Paint();
                    },
                    turnToken,
                    onToolEvent: ev =>
                    {
                        PushToolTrail(ev);
                        ThrottledPaint();
                    });

                await PumpScrollWhileAsync(agentTask);

                AgentTurnResult result = await agentTask;
                toolCalls = result.ToolCallCount;
                sw.Stop();

                if (assistantIndex < 0 && !string.IsNullOrEmpty(result.ResponseText))
                    PushAssistant(result.ResponseText);
                else if (assistantIndex >= 0)
                    lock (_gate) _messages[assistantIndex].Text = result.ResponseText ?? collected.ToString();

                if (!string.IsNullOrWhiteSpace(result.BudgetWarning))
                    PushSystem(result.BudgetWarning);

                string summary = ActivityStatus.SummarizeTurn(result.Elapsed, result.ToolCallCount, result.Failed || result.Cancelled);
                if (result.Cancelled)
                    summary += " · stopped";
                string budget = ConversationCompactor.BudgetStatus(result.EstimatedPromptTokens, result.ContextWindowTokens);
                if (!string.IsNullOrEmpty(budget))
                    summary += " · " + budget;
                PushStatus(summary);
            }

            _turnCount++;
            AutoSave();
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            PushSystem("Turn stopped.");
            PushStatus(ActivityStatus.SummarizeTurn(sw.Elapsed, toolCalls, failed: true));
            AutoSave();
        }
        catch (Exception ex)
        {
            sw.Stop();
            PushError(ex.Message);
            PushStatus(ActivityStatus.SummarizeTurn(sw.Elapsed, toolCalls, failed: true));
            AutoSave();
        }
        finally
        {
            _busy = false;
            _activity = string.Empty;
            _streamBuffer.Clear();
            _streamAssistantIndex = -1;
            _turnCts?.Dispose();
            _turnCts = null;
            _scrollFromBottom = 0;
            Paint(force: true);
        }
    }

    private void PushToolTrail(ToolEvent ev)
    {
        string mark = ev.Phase switch
        {
            ToolEventPhase.Started => "●",
            ToolEventPhase.Finished => "✓",
            ToolEventPhase.Denied => "✗",
            ToolEventPhase.Planned => "◇",
            _ => "·"
        };
        string line = $"{mark} {ev.ToolName}";
        if (!string.IsNullOrWhiteSpace(ev.Detail))
            line += "  " + Truncate(ev.Detail, 72);
        if (ev.Phase != ToolEventPhase.Started && !string.IsNullOrWhiteSpace(ev.ResultPreview))
            line += "  →  " + Truncate(ev.ResultPreview!, 48);
        PushSystem(line);
        if (ev.Phase == ToolEventPhase.Started)
            SetActivity(ToolCallingLoop.DescribeToolStart(new OpenRouterToolCall("x", ev.ToolName, "{}")));
    }

    private async Task<IReadOnlyList<int>?> PickCriticIssuesInteractiveAsync(
        IReadOnlyList<CriticIssue> blockingIssues,
        CancellationToken cancellationToken)
    {
        PushSystem("Critic findings (pick which to fix):");
        for (int i = 0; i < blockingIssues.Count; i++)
        {
            var iss = blockingIssues[i];
            PushSystem($"  {i + 1}. [{iss.Severity}] {iss.Summary}");
            if (!string.IsNullOrWhiteSpace(iss.Evidence))
                PushSystem($"      evidence: {Truncate(iss.Evidence, 120)}");
        }
        PushSystem("Type: all · none · 1,3  then Enter  (Esc = all)");

        _criticPickPrompt = "Critic pick › ";
        _criticPickBuffer = string.Empty;
        _criticPickTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetActivity(_criticPickPrompt + _criticPickBuffer);
        Paint(force: true);
        using var reg = cancellationToken.Register(() => _criticPickTcs.TrySetResult("all"));
        try
        {
            string answer = await _criticPickTcs.Task;
            answer = (answer ?? "all").Trim().ToLowerInvariant();
            if (answer is "none" or "skip" or "accept" or "0")
                return Array.Empty<int>();
            if (answer is "" or "all" or "*")
                return null;
            var indices = new List<int>();
            foreach (string part in answer.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int n) && n > 0)
                    indices.Add(n);
            }
            return indices.Count > 0 ? indices : null;
        }
        finally
        {
            _criticPickTcs = null;
            _criticPickBuffer = string.Empty;
            _criticPickPrompt = string.Empty;
        }
    }

    private async Task<bool> PromptToolApprovalAsync(ToolApprovalRequest req, CancellationToken token)
    {
        _approvalPrompt = $"Allow {req.ToolName}?  {Truncate(req.Summary, 60)}  [y/n]";
        SetActivity(_approvalPrompt);
        Paint(force: true);

        _approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => _approvalTcs.TrySetResult(false));
        try
        {
            return await _approvalTcs.Task;
        }
        finally
        {
            _approvalPrompt = string.Empty;
            _approvalTcs = null;
        }
    }

    private void TryAutoResumeLastSession()
    {
        if (_session == null)
            return;
        var list = _sessionStore.List(max: 1);
        if (list.Count == 0)
            return;
        // Only auto-resume if this is a brand-new empty chat and last session has content.
        lock (_gate)
        {
            if (_messages.Count > 1) // ready line may already be there — check history
                return;
        }
        if (_session.History.Count > 0)
            return;

        SessionListItem last = list[0];
        if (last.MessageCount < 2)
            return;
        if (TryLoadSession(last.Id, out _))
            PushSystem($"Resumed last session: {last.Title}");
    }

    public string UndoLastTurn()
    {
        if (_session == null)
            return "No session.";
        string summary = _session.ToolExecutor.Undo.UndoLast();
        PushSystem(summary);
        return summary;
    }

    public void HandleCheckpoint(string args)
    {
        if (_session == null)
            return;
        var wf = _session.ToolExecutor.Workflow;
        string a = (args ?? "").Trim();
        string root = _session.Workspace.PrimaryRoot;

        if (string.IsNullOrEmpty(a) || a.Equals("save", StringComparison.OrdinalIgnoreCase))
        {
            var paths = _session.ToolExecutor.WrittenPaths.ToList();
            if (paths.Count == 0)
            {
                var last = _session.ToolExecutor.Undo.PeekLastFiles();
                paths = last.Select(f => f.Path).ToList();
            }
            string id = wf.Checkpoints.CreateFromPaths("manual", root, paths);
            PushSystem($"Checkpoint saved: {id}  ({paths.Count} file(s)).  Restore: /checkpoint restore {id}");
            return;
        }

        string[] parts = a.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts[0].ToLowerInvariant();
        if (sub is "list" or "ls")
        {
            var list = wf.Checkpoints.List();
            if (list.Count == 0)
            {
                PushSystem("No checkpoints yet. Create with /checkpoint [name]");
                return;
            }
            PushSystem("Checkpoints:");
            int i = 1;
            foreach (var c in list)
                PushSystem($"  {i++}. {c.Id}  ·  {c.Name}  ·  {c.FileCount} file(s)  ·  {c.Utc.ToLocalTime():g}");
            PushSystem("Restore: /checkpoint restore <id|n>");
            return;
        }
        if (sub is "restore" or "load" or "apply")
        {
            string key = parts.Length >= 2 ? parts[1].Trim() : "";
            if (string.IsNullOrEmpty(key))
            {
                PushSystem("Usage: /checkpoint restore <id|n>");
                return;
            }
            PushSystem(wf.Checkpoints.Restore(key));
            return;
        }

        // /checkpoint <name> — named snapshot
        string name = a;
        var namedPaths = _session.ToolExecutor.WrittenPaths.ToList();
        if (namedPaths.Count == 0)
            namedPaths = _session.ToolExecutor.Undo.PeekLastFiles().Select(f => f.Path).ToList();
        string namedId = wf.Checkpoints.CreateFromPaths(name, root, namedPaths);
        PushSystem($"Checkpoint “{name}” saved: {namedId}  ({namedPaths.Count} file(s))");
    }

    public void HandlePlan(string args)
    {
        if (_session == null)
            return;
        var plan = _session.ToolExecutor.Workflow.Plan;
        string a = (args ?? "").Trim().ToLowerInvariant();
        if (a is "clear" or "reset")
        {
            plan.Clear();
            PushSystem("Plan board cleared.");
            return;
        }
        PushSystem(plan.ToDisplayBlock());
    }

    public void HandleChanges()
    {
        if (_session == null)
            return;
        PushSystem(_session.ToolExecutor.Workflow.Changes.Summarize());
    }

    public void HandleAccept(string args)
    {
        if (_session == null)
            return;
        var ch = _session.ToolExecutor.Workflow.Changes;
        string a = (args ?? "all").Trim().ToLowerInvariant();
        if (a is "all" or "*" or "")
        {
            PushSystem(ch.AcceptAll());
            return;
        }
        PushSystem(ch.Accept(ParseIndexList(a)));
    }

    public void HandleReject(string args)
    {
        if (_session == null)
            return;
        var ch = _session.ToolExecutor.Workflow.Changes;
        string a = (args ?? "all").Trim().ToLowerInvariant();
        if (a is "all" or "*" or "")
        {
            PushSystem(ch.RejectAll());
            return;
        }
        PushSystem(ch.Reject(ParseIndexList(a)));
    }

    public async Task HandleReplayAsync()
    {
        if (_session == null)
            return;
        if (!_session.ToolExecutor.Workflow.Replay.HasReplay)
        {
            PushSystem("No tool plan to replay. Run an agent turn first.");
            return;
        }
        PushSystem(_session.ToolExecutor.Workflow.Replay.Describe());
        SetActivity("Replaying tools…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            string result = await _session.ToolExecutor.Workflow.Replay.ReplayAsync(
                _session.ToolExecutor, cts.Token);
            PushSystem(result);
        }
        catch (Exception ex)
        {
            PushSystem("Replay failed: " + ex.Message);
        }
        finally
        {
            SetActivity(string.Empty);
            Paint(force: true);
        }
    }

    public void HandleJobs(string? id)
    {
        if (_session == null)
            return;
        PushSystem(_session.ToolExecutor.Workflow.Jobs.Status(id));
    }

    public void HandleWatch(string? arg)
    {
        if (_session == null)
            return;
        var watch = _session.ToolExecutor.Workflow.Watch;
        string a = (arg ?? "").Trim().ToLowerInvariant();
        string root = _session.Workspace.PrimaryRoot;
        if (a is "off" or "stop" or "0" or "false")
        {
            watch.Stop();
            PushSystem("Workspace watch stopped.");
            return;
        }
        if (a is "on" or "start" or "1" or "true" || string.IsNullOrEmpty(a))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                PushSystem("Lock a folder first (/browse) before enabling watch.");
                return;
            }
            watch.Start(root);
            PushSystem(watch.IsEnabled
                ? $"Watching {root} for external file changes."
                : "Could not start file watcher for that folder.");
            return;
        }
        if (a is "status" or "?")
        {
            PushSystem(watch.IsEnabled ? $"Watch on · {root}" : "Watch off.  /watch on");
            return;
        }
        PushSystem("Usage: /watch [on|off|status]");
    }

    public void HandleSticky(string args)
    {
        if (_session == null)
            return;
        var wf = _session.ToolExecutor.Workflow;
        string a = (args ?? "").Trim();
        if (string.IsNullOrEmpty(a) || a.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            PushSystem(wf.StickyStatus());
            return;
        }
        if (a.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || a.Equals("off", StringComparison.OrdinalIgnoreCase)
            || a.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            wf.ClearSticky();
            PushSystem("Sticky task cleared.");
            return;
        }

        // Optional trailing turn count: /sticky fix the login flow 8
        int turns = 8;
        string task = a;
        string[] words = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && int.TryParse(words[^1], out int n) && n > 0 && n <= 50)
        {
            turns = n;
            task = string.Join(' ', words.Take(words.Length - 1));
        }
        wf.SetSticky(task, turns);
        PushSystem($"Sticky for {turns} turn(s): {task}");
    }

    public async Task HandlePrAsync(string args)
    {
        if (_session == null)
            return;
        string title = string.IsNullOrWhiteSpace(args) ? "Axiom changes" : args.Trim();
        string body = "Opened from Axiom CLI (`/pr`).";
        SetActivity("Opening PR…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string result = await GitBranchContext.CreatePullRequestAsync(
                _session.Workspace.PrimaryRoot, title, body, cts.Token);
            PushSystem(result);
        }
        catch (Exception ex)
        {
            PushSystem("PR failed: " + ex.Message);
        }
        finally
        {
            SetActivity(string.Empty);
            Paint(force: true);
        }
    }

    public void HandleNetwork(string? arg)
    {
        if (_session == null)
            return;
        var net = _session.ToolExecutor.Network;
        string a = (arg ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(a) || a is "status" or "?")
        {
            string mode = net.Offline ? "offline" : (net.RequireApproval ? "ask" : "on");
            PushSystem($"Network: {mode}  ·  /network on | off | ask");
            return;
        }
        if (a is "on" or "online" or "1" or "true")
        {
            net.Offline = false;
            net.RequireApproval = false;
            PushSystem("Network on — download/fetch/web_search allowed (still respect approval mode).");
            return;
        }
        if (a is "off" or "offline" or "0" or "false")
        {
            net.Offline = true;
            PushSystem("Network offline — outbound tools blocked.");
            return;
        }
        if (a is "ask" or "prompt")
        {
            net.Offline = false;
            net.RequireApproval = true;
            PushSystem("Network ask — outbound tools require confirmation.");
            return;
        }
        PushSystem("Usage: /network [on|off|ask|status]");
    }

    public void HandlePolicy()
    {
        if (_session == null)
            return;
        _session.ToolExecutor.ReloadPolicies();
        string path = Path.Combine(_session.Workspace.PrimaryRoot, ".axiom", "shell-policy.json");
        PushSystem("Shell policy:");
        PushSystem("  Built-in denies: force-push, rm -rf /, curl|sh, drop database, …");
        PushSystem(File.Exists(path)
            ? $"  Project overrides: {path}"
            : $"  Optional overrides: create {path} with {{ \"allow\": [], \"deny\": [] }}");
        PushSystem("  Secrets in tool output are auto-redacted (API keys, tokens, JWTs).");
        PushSystem($"  Network: {(_session.ToolExecutor.Network.Offline ? "offline" : _session.ToolExecutor.Network.RequireApproval ? "ask" : "on")}");
        var fails = _session.ToolExecutor.Workflow.FailedTests;
        if (fails.Count > 0)
            PushSystem("  Regression guard: " + string.Join(", ", fails.Take(6)));
    }

    public void HandleSpec(string? title)
    {
        if (_session == null)
            return;
        string root = _session.Workspace.PrimaryRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            PushSystem("Lock a folder first (/browse) so SPEC.md has a place to land.");
            return;
        }

        string md = IntelligenceHelpers.BuildSpecMarkdown(_session.History, title);
        string path = Path.Combine(root, "SPEC.md");
        try
        {
            File.WriteAllText(path, md);
            PushSystem($"Wrote {path} from this session. Ask Axiom to implement from SPEC.md when ready.");
        }
        catch (Exception ex)
        {
            PushSystem("Could not write SPEC.md: " + ex.Message);
        }
    }

    public void HandleRepoMap()
    {
        if (_session == null)
            return;
        string root = _session.Workspace.PrimaryRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            PushSystem("Lock a folder first (/browse).");
            return;
        }
        string map = RepoMapService.Build(root, maxChars: 2500);
        PushSystem(string.IsNullOrWhiteSpace(map) ? "(empty repo map)" : map);
    }

    public void HandleCouncil(string args)
    {
        if (_session == null)
            return;
        var t = _session.Tools;
        string a = (args ?? "").Trim();
        if (string.IsNullOrEmpty(a) || a is "status" or "?")
        {
            PushSystem($"Council: {(t.CouncilEnabled ? "on" : "off")} · {t.CouncilLabel}");
            PushSystem($"  severity: {CriticSeverity.Describe(t.CriticSeverity)}   ·  /council severity strict|high|critical");
            PushSystem($"  explore: {(t.ParallelExplore ? "on" : "off")}  ·  loop: {(t.UserInLoopCritic ? "on" : "off")}  ·  post-merge: {(t.PostMergeCritic ? "on" : "off")}");
            PushSystem($"  roles: {(t.RoleVisibility == CouncilRoleVisibility.FinalOnly ? "final" : "full")}   ·  /council roles full|final");
            PushSystem("  acceptance: .axiom/acceptance.md (auto-loaded when present)");
            return;
        }

        string[] parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string head = parts[0].ToLowerInvariant();
        string? val = parts.Length >= 2 ? parts[1].ToLowerInvariant() : null;

        switch (head)
        {
            case "on":
            case "off":
                t.CouncilEnabled = head == "on";
                PushSystem($"Council {(t.CouncilEnabled ? "on" : "off")}.");
                break;
            case "severity":
            case "sev":
                if (val != null && CriticSeverity.TryParse(val, out var pol))
                {
                    t.CriticSeverity = pol;
                    PushSystem($"Critic severity → {CriticSeverity.Describe(pol)}");
                }
                else
                    PushSystem("Usage: /council severity strict|high|critical");
                break;
            case "explore":
                t.ParallelExplore = val is null or "on" or "1" or "true";
                if (val is "off" or "0" or "false")
                    t.ParallelExplore = false;
                PushSystem($"Parallel explore → {(t.ParallelExplore ? "on" : "off")}");
                break;
            case "loop":
            case "interactive":
                t.UserInLoopCritic = val is null or "on" or "1" or "true";
                if (val is "off" or "0" or "false")
                    t.UserInLoopCritic = false;
                PushSystem($"User-in-loop Critic → {(t.UserInLoopCritic ? "on" : "off")}");
                break;
            case "post":
            case "postmerge":
            case "post-merge":
                t.PostMergeCritic = val is null or "on" or "1" or "true";
                if (val is "off" or "0" or "false")
                    t.PostMergeCritic = false;
                PushSystem($"Post-merge Critic → {(t.PostMergeCritic ? "on" : "off")}");
                break;
            case "roles":
            case "visibility":
                if (val is "final" or "finalonly" or "collapse")
                {
                    t.RoleVisibility = CouncilRoleVisibility.FinalOnly;
                    PushSystem("Role visibility → final only (hide Architect/Critic cards).");
                }
                else
                {
                    t.RoleVisibility = CouncilRoleVisibility.Full;
                    PushSystem("Role visibility → full (show Architect/Critic/Explore).");
                }
                break;
            default:
                PushSystem("Usage: /council [status|severity …|explore on|loop on|post on|roles full|final]");
                break;
        }
    }

    private bool DrainWorkflowNotifications()
    {
        if (_session == null)
            return false;
        var wf = _session.ToolExecutor.Workflow;
        bool any = false;
        foreach (string n in wf.Jobs.DrainNotifications())
        {
            PushSystem("⏳ " + n);
            any = true;
        }
        var watchEvents = wf.Watch.Drain(8);
        if (watchEvents.Count > 0)
        {
            PushSystem("👁 Workspace: " + string.Join(" · ", watchEvents));
            any = true;
        }
        return any;
    }

    private static IEnumerable<int> ParseIndexList(string raw)
    {
        foreach (string part in raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int n) && n > 0)
                yield return n;
        }
    }

    // Architect → Builder → Critic pipeline (mirrors desktop Workplace council control flow).
    private async Task RunCouncilTurnAsync(string grounded, Stopwatch sw, CancellationToken token)
    {
        if (_session == null)
            return;

        SetActivity("Council · Architect planning");
        Paint();
        _streamBuffer.Clear();
        _streamAssistantIndex = -1;

        while (_pendingCouncilEvents.TryDequeue(out _)) { }
        IProgress<CouncilEvent> progress = new InlineProgress<CouncilEvent>(
            evt => _pendingCouncilEvents.Enqueue(evt));

        ConnectedWorkspaceState? wsState = null;
        if (_session.Workspace.Roots.Count > 0)
        {
            try
            {
                // Full folder connection (index + ConnectionKind=Folder) so council never sees "None".
                // Auto-apply lets patch envelopes land on disk after Critic (write_file already writes live).
                var access = new WorkspaceAccessService();
                wsState = access.CreateFolderConnection(_session.Workspace.PrimaryRoot);
                wsState.AutoApplyCodebaseChanges = true;
                SetActivity($"Workspace · {wsState.IndexedFileCount} file(s) indexed");
                Paint();
            }
            catch (Exception ex)
            {
                // Still attach RootPath so access language can fire even if indexing throws.
                wsState = new ConnectedWorkspaceState
                {
                    CodebaseEditAccessEnabled = true,
                    AutoApplyCodebaseChanges = true,
                    ConnectionKind = WorkspaceConnectionKind.Folder.ToString(),
                    RootPath = _session.Workspace.PrimaryRoot,
                    StatusMessage = "Folder connected (index incomplete: " + ex.Message + ")"
                };
            }
        }

        CouncilOrchestrator council = _session.CreateCouncil();
        CriticIssuePicker? picker = _session.Tools.UserInLoopCritic
            ? PickCriticIssuesInteractiveAsync
            : null;
        Task<CouncilResult> task = council.RunAsync(
            new CouncilRequest(grounded, wsState, _session.CouncilTools(), picker),
            progress,
            token);

        await PumpScrollWhileAsync(task);
        DrainCouncilEvents();
        CouncilResult result = await task;
        sw.Stop();

        string final = result.FinalText ?? string.Empty;
        if (result.ChangedFiles is { Count: > 0 })
        {
            var files = string.Join("\n", result.ChangedFiles.Select(f => "  • " + f));
            string applyNote = string.IsNullOrWhiteSpace(result.ApplySummary)
                ? $"Files written to workspace:\n{files}"
                : $"{result.ApplySummary}\n{files}";
            PushSystem(applyNote);
        }

        PushAssistant(string.IsNullOrWhiteSpace(final)
            ? (result.Cancelled ? "(Stopped.)" : "(Council produced no text.)")
            : final);
        _session.History.Add(new OpenRouterMessage("user", grounded));
        _session.History.Add(new OpenRouterMessage("assistant", final));

        int toolish = Math.Max(3, result.ToolCallCount); // roles + tool calls
        string summary = result.Success && !result.Cancelled
            ? ActivityStatus.SummarizeTurn(sw.Elapsed, toolish)
            : ActivityStatus.SummarizeTurn(sw.Elapsed, toolish, failed: true);
        if (result.Cancelled)
            summary += " · stopped";
        if (result.ToolCallCount > 0)
            summary += $" · {result.ToolCallCount} tool call(s)";
        if (result.ChangedFiles is { Count: > 0 })
            summary += $" · {result.ChangedFiles.Count} file(s) on disk";
        if (result.FinalCriticReport != null && result.FinalCriticReport.HasIssues)
            summary += $" · Critic: {result.FinalCriticReport.FindingsCount} issue(s)";
        PushStatus("Council · " + summary);
    }

    private void AppendStreamToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        _streamBuffer.Append(token);
        // Live stream into a temporary system/assistant line while a role is generating.
        lock (_gate)
        {
            string text = _streamBuffer.ToString();
            if (_streamAssistantIndex < 0 || _streamAssistantIndex >= _messages.Count)
            {
                _messages.Add(new Msg { Role = Role.System, Text = text });
                _streamAssistantIndex = _messages.Count - 1;
            }
            else
            {
                _messages[_streamAssistantIndex].Text = text;
            }
        }
        SetActivity(ActivityStatus.Generating);
    }

    private void FlushStreamAsSystem(string label)
    {
        if (_streamBuffer.Length == 0)
            return;
        string text = _streamBuffer.ToString();
        _streamBuffer.Clear();
        lock (_gate)
        {
            if (_streamAssistantIndex >= 0 && _streamAssistantIndex < _messages.Count)
                _messages.RemoveAt(_streamAssistantIndex);
            _streamAssistantIndex = -1;
        }
        // Keep a short breadcrumb; full text may already be reported as Architect/Critic output.
        if (text.Length > 40)
            PushSystem($"{label} streamed ({text.Length} chars)");
    }

    private void DrainCouncilEvents()
    {
        bool changed = false;
        while (_pendingCouncilEvents.TryDequeue(out CouncilEvent evt))
        {
            changed = true;
            switch (evt.Kind)
            {
                case CouncilEventKind.Status:
                case CouncilEventKind.Warning:
                    SetActivity(evt.Message);
                    break;
                case CouncilEventKind.Tool:
                    PushSystem(evt.Message);
                    break;
                case CouncilEventKind.Token:
                    AppendStreamToken(evt.Message);
                    break;
                case CouncilEventKind.ArchitectOutput:
                    SetActivity("Council · Architect done");
                    FlushStreamAsSystem("Architect plan");
                    if (_session?.Tools.RoleVisibility != CouncilRoleVisibility.FinalOnly)
                        PushSystem("Architect plan\n" + Truncate(evt.Message, 1200));
                    break;
                case CouncilEventKind.BuilderOutput:
                    SetActivity("Council · Builder working");
                    FlushStreamAsSystem("Builder draft");
                    break;
                case CouncilEventKind.CriticOutput:
                    SetActivity("Council · Critic reviewing");
                    FlushStreamAsSystem("Critic");
                    if (_session?.Tools.RoleVisibility != CouncilRoleVisibility.FinalOnly)
                        PushSystem("Critic review\n" + Truncate(evt.Message, 800));
                    break;
                case CouncilEventKind.ExploreOutput:
                    SetActivity("Council · Explore done");
                    if (_session?.Tools.RoleVisibility != CouncilRoleVisibility.FinalOnly)
                        PushSystem("Explore lane\n" + Truncate(evt.Message, 800));
                    break;
                case CouncilEventKind.Completed:
                    SetActivity("Council · Finished");
                    break;
                case CouncilEventKind.Failed:
                    SetActivity("Council · Failed");
                    break;
            }
        }

        if (changed)
            ThrottledPaint();
    }

    private async Task PumpScrollWhileAsync(Task task)
    {
        while (!task.IsCompleted)
        {
            DrainCouncilEvents();
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo k = Console.ReadKey(intercept: true);
                MenuMode mode = GetMenuMode(_input, _cursor);
                if (TryConsumeMouseWheel(k, out int wheelDelta))
                    ScrollBy(wheelDelta);
                else
                    TryHandleScrollKey(k, mode);
                Paint();
            }
            await Task.Delay(20);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private void AutoSave()
    {
        if (_session == null)
            return;
        try
        {
            List<Msg> snap;
            lock (_gate) snap = _messages.ToList();

            string? firstUser = snap.FirstOrDefault(m => m.Role == Role.User)?.Text;
            var stored = new StoredSession
            {
                Id = _sessionId,
                Title = !string.IsNullOrWhiteSpace(_sessionTitle)
                    ? _sessionTitle!
                    : SessionStore.MakeTitle(firstUser),
                CreatedAt = _sessionCreated,
                UpdatedAt = DateTime.UtcNow,
                ModelId = _session.ModelId,
                ModelLabel = _session.ModelLabel,
                WorkspaceRoots = _session.Workspace.Roots.ToList(),
                WorkspaceExclusive = _session.Workspace.IsExclusive,
                Messages = snap.Select(m => new StoredUiMessage
                {
                    Role = m.Role.ToString().ToLowerInvariant(),
                    Text = m.Text
                }).ToList(),
                History = _session.History.Select(h => new OpenRouterMessageDto
                {
                    Role = h.Role,
                    Text = h.Text
                }).ToList()
            };
            _sessionStore.Save(stored);
        }
        catch
        {
            // Persistence must never break a chat turn.
        }
    }

    private void SetActivity(string status)
    {
        _activity = status;
        _animFrames = ResolveAnim(status);
    }

    private void PersistProfileFromSession()
    {
        if (_session == null || _profiles == null || _profile == null)
            return;
        try
        {
            _profile.DefaultModelId = _session.ModelId;
            _profile.DefaultModelLabel = _session.ModelLabel;
            _profile.ApprovalMode = UserProfileStore.FormatApproval(_session.Tools.ApprovalMode);
            _profile.CouncilEnabled = _session.Tools.CouncilEnabled;
            _profile.WebSearchEnabled = _session.Tools.WebSearchEnabled;
            _profile.SandboxEnabled = _session.Tools.SandboxEnabled;
            _profile.CalculatorEnabled = _session.Tools.CalculatorEnabled;
            _profile.WorkspaceRoots = _session.Workspace.Roots.ToList();
            _profile.WorkspaceExclusive = _session.Workspace.IsExclusive;
            _profiles.Save(_profile);
        }
        catch { /* never break chat */ }
    }

    private void MarkOnboarding(string step)
    {
        if (_profile == null || _profiles == null || _profile.OnboardingComplete)
            return;
        if (_profile.OnboardingStepsDone.Add(step))
        {
            if (_profile.OnboardingStepsDone.Contains("api_key")
                && _profile.OnboardingStepsDone.Contains("folder")
                && _profile.OnboardingStepsDone.Contains("first_message")
                && _profile.OnboardingStepsDone.Contains("hints"))
            {
                _profile.OnboardingComplete = true;
            }
            try { _profiles.Save(_profile); } catch { /* ignore */ }
        }
    }

    public void ShowOnboardingIfNeeded()
    {
        if (_session == null || _profile == null || _profile.OnboardingComplete)
            return;

        PushSystem("── Getting started ──────────────────────────────────");
        bool hasKey = _session.ChatService.HasValidKey || _session.ChatService.HasValidCustomEndpoint;
        bool hasFolder = _session.Workspace.IsExclusive || _session.Workspace.Roots.Count > 0;
        PushSystem($"{(hasKey ? "✓" : "1.")} API key  {(hasKey ? "(done)" : "— paste when prompted or /help")}");
        PushSystem($"{(hasFolder ? "✓" : "2.")} Lock a folder  — /browse or @ Browse…");
        PushSystem("3. Send a message  — ask Axiom to edit or explain something");
        PushSystem("4. Keys: Esc stop · Ctrl+K · /checkpoint · /plan · /sticky · /undo · /mode ask");
        PushSystem("────────────────────────────────────────────────────");
        if (hasKey)
            MarkOnboarding("api_key");
        if (hasFolder)
            MarkOnboarding("folder");
        MarkOnboarding("hints");
    }

    private async Task<bool> HandlePaletteKeyAsync(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            _paletteOpen = false;
            _paletteQuery = string.Empty;
            return true;
        }

        var items = CommandPalette.Filter(CommandPalette.BuildCore(), _paletteQuery);
        if (key.Key == ConsoleKey.UpArrow)
        {
            if (items.Count > 0)
                _paletteIndex = (_paletteIndex - 1 + items.Count) % items.Count;
            return true;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            if (items.Count > 0)
                _paletteIndex = (_paletteIndex + 1) % items.Count;
            return true;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            if (items.Count == 0)
                return true;
            var pick = items[Math.Clamp(_paletteIndex, 0, items.Count - 1)];
            _paletteOpen = false;
            _paletteQuery = string.Empty;
            await RunPaletteActionAsync(pick.Action);
            return true;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (_paletteQuery.Length > 0)
            {
                _paletteQuery = _paletteQuery[..^1];
                _paletteIndex = 0;
            }
            return true;
        }
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            _paletteQuery += key.KeyChar;
            _paletteIndex = 0;
            return true;
        }
        return true;
    }

    private async Task RunPaletteActionAsync(string action)
    {
        if (action == "__cycle_mode__")
        {
            CycleApprovalMode();
            return;
        }
        if (action == "__session_picker__")
        {
            OpenSessionPicker();
            return;
        }
        if (action == "/exit" || action.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            _running = false;
            return;
        }
        if (action.StartsWith('/') && _handleSlash != null && _session != null)
        {
            // Commands that need free text stay as prefilled input.
            if (action is "/rename ")
            {
                _input = "/rename ";
                _cursor = _input.Length;
                return;
            }
            await _handleSlash(action.Trim(), _session);
            if (action.StartsWith("/mode", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("/tools", StringComparison.OrdinalIgnoreCase)
                || action.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
                PersistProfileFromSession();
            return;
        }
        await SubmitAsync(action);
    }

    private async Task<bool> HandleSessionPickerKeyAsync(ConsoleKeyInfo key)
    {
        var list = _sessionStore.List(max: 40);
        if (key.Key == ConsoleKey.Escape)
        {
            _sessionPickerOpen = false;
            return true;
        }
        if (list.Count == 0)
            return true;

        if (key.Key == ConsoleKey.UpArrow)
        {
            _sessionPickerIndex = (_sessionPickerIndex - 1 + list.Count) % list.Count;
            return true;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            _sessionPickerIndex = (_sessionPickerIndex + 1) % list.Count;
            return true;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            var item = list[Math.Clamp(_sessionPickerIndex, 0, list.Count - 1)];
            _sessionPickerOpen = false;
            if (TryLoadSession(item.Id, out string err))
                PushSystem($"Loaded: {item.Title}");
            else
                PushError(err);
            return true;
        }
        if (key.KeyChar is 'd' or 'D' or 'x' or 'X')
        {
            var item = list[Math.Clamp(_sessionPickerIndex, 0, list.Count - 1)];
            bool wasCurrent = string.Equals(item.Id, _sessionId, StringComparison.OrdinalIgnoreCase);
            if (_sessionStore.Delete(item.Id))
            {
                PushSystem($"Deleted: {item.Title}");
                if (wasCurrent)
                    DeleteCurrentAndStartFresh();
                if (_sessionPickerIndex >= Math.Max(1, list.Count - 1))
                    _sessionPickerIndex = Math.Max(0, list.Count - 2);
            }
            return true;
        }
        await Task.CompletedTask;
        return true;
    }

    private void PaintCommandPalette(string[] rows, int top, int height, int w)
    {
        var items = CommandPalette.Filter(CommandPalette.BuildCore(), _paletteQuery);
        rows[top] = Ansi.Fg(AxiomTheme.Gold) + Ansi.Bold
            + Ansi.ClipPad($" ⌘ Command palette  / {_paletteQuery}█", w) + Ansi.Reset;
        int show = Math.Max(1, height - 1);
        int sel = items.Count == 0 ? 0 : Math.Clamp(_paletteIndex, 0, items.Count - 1);
        int from = Math.Clamp(sel - show / 2, 0, Math.Max(0, items.Count - show));
        for (int i = 0; i < show; i++)
        {
            int row = top + 1 + i;
            if (row >= rows.Length)
                break;
            if (from + i >= items.Count)
            {
                rows[row] = string.Empty;
                continue;
            }
            var item = items[from + i];
            bool active = from + i == sel;
            string line = $" {(active ? "❯" : " ")} {item.Label,-22}  {item.Description}";
            rows[row] = (active ? Ansi.Fg(AxiomTheme.Gold) : Ansi.Fg(AxiomTheme.TextSecondary))
                + Ansi.ClipPad(line, w) + Ansi.Reset;
        }
    }

    private void PaintSessionPicker(string[] rows, int top, int height, int w)
    {
        var list = _sessionStore.List(max: 40);
        rows[top] = Ansi.Fg(AxiomTheme.Gold) + Ansi.Bold
            + Ansi.ClipPad(" ☰ Sessions  ·  Enter load  ·  d delete", w) + Ansi.Reset;
        if (list.Count == 0)
        {
            rows[top + 1] = Ansi.Fg(AxiomTheme.SystemMuted) + Ansi.ClipPad("  (no saved sessions)", w) + Ansi.Reset;
            return;
        }
        int show = Math.Max(1, height - 1);
        int sel = Math.Clamp(_sessionPickerIndex, 0, list.Count - 1);
        int from = Math.Clamp(sel - show / 2, 0, Math.Max(0, list.Count - show));
        for (int i = 0; i < show; i++)
        {
            int row = top + 1 + i;
            if (row >= rows.Length || from + i >= list.Count)
                continue;
            var item = list[from + i];
            bool active = from + i == sel;
            bool cur = string.Equals(item.Id, _sessionId, StringComparison.OrdinalIgnoreCase);
            string line = $" {(active ? "❯" : " ")} {from + i + 1,2}. {item.Title}  ·  {item.UpdatedAt.ToLocalTime():g}"
                + (cur ? "  ← current" : "");
            rows[row] = (active ? Ansi.Fg(AxiomTheme.Gold) : Ansi.Fg(AxiomTheme.TextSecondary))
                + Ansi.ClipPad(line, w) + Ansi.Reset;
        }
    }

    private static string[] ResolveAnim(string label)
    {
        string l = (label ?? string.Empty).ToLowerInvariant();
        if (l.Contains("fail") || l.Contains("error") || l.Contains("stopped"))
            return ["✕  ", " ✕ ", "  ✕", " ✕ "];
        if (l.Contains("complete") || l.Contains("finished"))
            return ["✓  ", " ✓ ", "  ✓", " ✓ "];
        if (l.Contains("download"))
            return ["→   ", " →  ", "  → ", "   →", "  → ", " →  "];
        if (l.Contains("search"))
            return ["·   ", "··  ", "··· ", "····", " ···", "  ··", "   ·"];
        if (l.Contains("build") || l.Contains("run") || l.Contains("compil") || l.Contains("command"))
            return ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█", "▇", "▆", "▅", "▄", "▃", "▂"];
        if (l.Contains("writ") || l.Contains("read") || l.Contains("list"))
            return ["▓░░░", "░▓░░", "░░▓░", "░░░▓", "░░▓░", "░▓░░"];
        if (l.Contains("generat") || l.Contains("stream"))
            return ["◐", "◓", "◑", "◒"];
        if (l.Contains("work") || l.Contains("step"))
            return ["✧", "✦", "✶", "✦"];
        return ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    }

    private void ThrottledPaint()
    {
        if ((DateTime.UtcNow - _lastPaint).TotalMilliseconds < 40)
            return;
        Paint();
    }

    private void Paint(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastPaint).TotalMilliseconds < 16)
            return;
        _lastPaint = DateTime.UtcNow;

        _screen.RefreshSize();
        int w = _screen.Width;
        int h = _screen.Height;

        int headerH = 2;
        int activityH = 1;
        int inputH = ComputeInputHeight(w);
        int menuH = ComputeMenuHeight(w);
        int bottomH = activityH + menuH + inputH;
        int viewportH = Math.Max(3, h - headerH - bottomH);

        var rows = new string[h];
        for (int i = 0; i < h; i++)
            rows[i] = string.Empty;

        // ── Header (always: model · mode · council · workspace · profile · context) ──
        var session = _session;
        (int used, int max) = session?.EstimateContext() ?? (0, 0);
        string model = session?.ModelLabel ?? "—";
        string ctx = ConsoleUi.FormatContext(used, max);
        string modeLabel = session?.Tools.ApprovalLabel ?? "auto";
        string council = session == null ? "—" : session.Tools.CouncilLabel;
        string wsHint = session?.Workspace.Roots.Count > 0
            ? (session.Workspace.IsExclusive ? "🔒 " : "") + ShortName(session.Workspace.PrimaryRoot)
            : "no-folder";
        string titleBit = !string.IsNullOrWhiteSpace(_sessionTitle) ? $" · {_sessionTitle}" : "";
        string stickyBit = !string.IsNullOrWhiteSpace(session?.ToolExecutor.Workflow.StickyTask)
            ? " · sticky"
            : "";
        string watchBit = session?.ToolExecutor.Workflow.Watch.IsEnabled == true ? " · watch" : "";
        int runningJobs = session?.ToolExecutor.Workflow.Jobs.List()
            .Count(j => j.State == BackgroundJobState.Running) ?? 0;
        string jobsBit = runningJobs > 0 ? $" · jobs:{runningJobs}" : "";
        string left = $" ◆ Axiom v{GetVersion()} · {model} · {modeLabel} · {council} · {wsHint}{stickyBit}{watchBit}{jobsBit}{titleBit}";
        string right = $"@{_profileName}  {ctx} ";
        string header = ConsoleUi.LayoutThree(left, "", right, w);
        rows[0] = Ansi.Fg(AxiomTheme.Gold) + Ansi.Bold + Ansi.ClipPad(header, w) + Ansi.Reset;
        rows[1] = Ansi.Fg(AxiomTheme.Border) + new string('═', w) + Ansi.Reset;

        // Palette / session picker overlays take the viewport when open.
        if (_paletteOpen)
        {
            PaintCommandPalette(rows, headerH, viewportH, w);
            int activityRowP = headerH + viewportH;
            rows[activityRowP] = Ansi.Fg(AxiomTheme.SystemMuted)
                + Ansi.ClipPad(" Ctrl+K palette · ↑↓ · Enter run · Esc close · type to filter ", w) + Ansi.Reset;
            int boxTopP = h - inputH;
            PaintInputBox(rows, boxTopP, w, model, ctx);
            _screen.Paint(rows);
            return;
        }
        if (_sessionPickerOpen)
        {
            PaintSessionPicker(rows, headerH, viewportH, w);
            int activityRowS = headerH + viewportH;
            rows[activityRowS] = Ansi.Fg(AxiomTheme.SystemMuted)
                + Ansi.ClipPad(" Sessions · ↑↓ · Enter load · d delete · Esc close ", w) + Ansi.Reset;
            int boxTopS = h - inputH;
            PaintInputBox(rows, boxTopS, w, model, ctx);
            _screen.Paint(rows);
            return;
        }

        // ── Message viewport ────────────────────────────────────
        List<string> transcript = BuildTranscriptLines(w);
        int maxScroll = Math.Max(0, transcript.Count - viewportH);
        _scrollFromBottom = Math.Clamp(_scrollFromBottom, 0, maxScroll);
        int end = transcript.Count - _scrollFromBottom;
        int start = Math.Max(0, end - viewportH);
        for (int i = 0; i < viewportH; i++)
        {
            int idx = start + i;
            string line = idx < transcript.Count ? transcript[idx] : string.Empty;
            rows[headerH + i] = line;
        }

        // Scroll hint when not at bottom
        if (_scrollFromBottom > 0 && viewportH > 0)
        {
            string hint = $" ↑ scrolled {_scrollFromBottom} · ↓ or PgDn to return ";
            int row = headerH + viewportH - 1;
            rows[row] = Ansi.Fg(AxiomTheme.Gold) + Ansi.ClipPad(hint.PadLeft((w + hint.Length) / 2).PadRight(w), w) + Ansi.Reset;
        }

        // ── Activity ────────────────────────────────────────────
        int activityRow = headerH + viewportH;
        if (_busy && !string.IsNullOrEmpty(_activity))
        {
            string glyph = _animFrames[Math.Abs(_animFrame) % _animFrames.Length];
            string act = string.IsNullOrEmpty(_approvalPrompt)
                ? $" {glyph}  {_activity}  ·  Esc to stop"
                : $" {glyph}  {_approvalPrompt}";
            rows[activityRow] = Ansi.Fg(AxiomTheme.Gold) + Ansi.ClipPad(act, w) + Ansi.Reset;
        }
        else
        {
            string tip = !string.IsNullOrEmpty(_approvalPrompt)
                ? " " + _approvalPrompt
                : " Ctrl+K palette · Ctrl+Shift+M mode · ↑↓ scroll · Esc stop · /continue · /export ";
            rows[activityRow] = Ansi.Fg(AxiomTheme.SystemMuted) + Ansi.ClipPad(tip, w) + Ansi.Reset;
        }

        // ── Menu (above input) ──────────────────────────────────
        int menuRow = activityRow + 1;
        MenuMode mode = GetMenuMode(_input, _cursor);
        var menuItems = mode == MenuMode.None ? Array.Empty<ChatInput.MenuItem>() : GetMenuItems(mode);
        if (mode != MenuMode.None)
        {
            string hint = mode == MenuMode.At
                ? " ↑↓ folders · Enter lock workspace · Esc cancel"
                : " ↑↓ navigate · Enter select · Esc clear";
            rows[menuRow++] = Ansi.Fg(AxiomTheme.SystemMuted) + Ansi.ClipPad(hint, w) + Ansi.Reset;
            if (menuItems.Count == 0)
            {
                rows[menuRow++] = Ansi.Fg(AxiomTheme.SystemMuted) + Ansi.ClipPad("  (no matches)", w) + Ansi.Reset;
            }
            else
            {
                int show = Math.Min(menuItems.Count, Math.Max(1, menuH - 1));
                int sel = Math.Clamp(_menuIndex, 0, menuItems.Count - 1);
                int from = Math.Clamp(sel - show / 2, 0, Math.Max(0, menuItems.Count - show));
                for (int i = 0; i < show; i++)
                {
                    var item = menuItems[from + i];
                    bool active = from + i == sel;
                    string marker = active ? "❯" : " ";
                    string status = item.IsTool
                        ? (item.Enabled == true ? " ● on " : " ○ off")
                        : "      ";
                    string line = mode == MenuMode.At
                        ? $"  {marker} {item.Label}"
                        : $"  {marker} {item.Label,-12}{status}  {item.Description}";
                    rows[menuRow++] = (active ? Ansi.Fg(AxiomTheme.Gold) : Ansi.Fg(AxiomTheme.TextSecondary))
                        + Ansi.ClipPad(line, w) + Ansi.Reset;
                }
            }
        }

        // ── Input box (bottom) ──────────────────────────────────
        int boxTop = h - inputH;
        PaintInputBox(rows, boxTop, w, model, ctx);

        _screen.Paint(rows);

        // Cursor inside input content
        PlaceInputCursor(boxTop, w);
    }

    private void PaintInputBox(string[] rows, int boxTop, int w, string model, string ctx)
    {
        string border = Ansi.Fg(AxiomTheme.Border);
        string primary = Ansi.Fg(AxiomTheme.TextPrimary);
        string muted = Ansi.Fg(AxiomTheme.SystemMuted);
        string gold = Ansi.Fg(AxiomTheme.Gold);
        int inner = Math.Max(10, w - 4);

        rows[boxTop] = border + "╭" + new string('─', Math.Max(1, w - 2)) + "╮" + Ansi.Reset;

        string display = string.IsNullOrEmpty(_input)
            ? "Message Axiom…  (/ tools · @ folders · Enter send)"
            : _input;
        List<string> content = Wrap(display, inner);
        while (content.Count < 2)
            content.Add(string.Empty);
        // Cap content rows to remaining box height - 2 (borders) - 1 (model)
        int contentRows = Math.Max(2, inputContentRows(w));
        while (content.Count < contentRows)
            content.Add(string.Empty);
        if (content.Count > contentRows)
            content = content.Take(contentRows).ToList();

        bool placeholder = string.IsNullOrEmpty(_input);
        for (int i = 0; i < content.Count; i++)
        {
            string body = content[i];
            if (body.Length < inner) body = body.PadRight(inner);
            else if (body.Length > inner) body = body[..inner];
            string fg = placeholder ? muted : primary;
            rows[boxTop + 1 + i] = border + "│ " + Ansi.Reset + fg + body + Ansi.Reset + border + " │" + Ansi.Reset;
        }

        int bottomBorderRow = boxTop + 1 + content.Count;
        rows[bottomBorderRow] = border + "╰" + new string('─', Math.Max(1, w - 2)) + "╯" + Ansi.Reset;

        // Model line with spaced context
        string left = $"  Model  {model}";
        int gap = Math.Max(10, w - left.Length - ctx.Length - 2);
        int dots = Math.Clamp(gap / 4, 3, 9);
        int lp = Math.Max(3, (gap - dots) / 2);
        int rp = Math.Max(3, gap - dots - lp);
        string modelLine = left + new string(' ', lp) + new string('·', dots) + new string(' ', rp) + ctx;
        rows[bottomBorderRow + 1] =
            muted + "  Model  " + Ansi.Reset +
            gold + Ansi.Bold + model + Ansi.Reset +
            muted + new string(' ', lp) + new string('·', dots) + new string(' ', rp) + ctx + Ansi.Reset;
        // Ensure full width
        rows[bottomBorderRow + 1] = FitColored(rows[bottomBorderRow + 1], w);
        _ = modelLine;
    }

    private void PlaceInputCursor(int boxTop, int w)
    {
        if (_busy)
        {
            _screen.HideCursor();
            return;
        }

        int inner = Math.Max(10, w - 4);
        (int row, int col) = CursorToRowCol(string.IsNullOrEmpty(_input) ? string.Empty : _input, _cursor, inner);
        int contentRows = inputContentRows(w);
        row = Math.Clamp(row, 0, contentRows - 1);
        // boxTop is 0-based row index; ANSI is 1-based. Content starts one row below the top border.
        _screen.ShowCursorAt(boxTop + 2 + row, Math.Min(w, 3 + col));
    }

    private int inputContentRows(int w)
    {
        // Must actually measure the wrapped input, not return a fixed constant -- otherwise
        // PaintInputBox's content.Take(contentRows) silently drops every wrapped line past the
        // 2nd, and the cursor gets clamped to row 0-1 forever once typing goes past it.
        int inner = Math.Max(10, w - 4);
        string display = string.IsNullOrEmpty(_input)
            ? "Message Axiom…  (/ tools · @ folders · Enter send)"
            : _input;
        int wrapped = Wrap(display, inner).Count;
        int maxRows = Math.Max(2, _screen.Height / 2); // leave room for header/transcript/menu
        return Math.Clamp(wrapped, 2, maxRows);
    }

    private int ComputeInputHeight(int w) => 1 + inputContentRows(w) + 1 + 1; // top + content + bottom + model

    private int ComputeMenuHeight(int w)
    {
        MenuMode mode = GetMenuMode(_input, _cursor);
        if (mode == MenuMode.None || _busy)
            return 0;
        var items = GetMenuItems(mode);
        int n = items.Count == 0 ? 1 : Math.Min(items.Count, 6);
        return 1 + n; // hint + items
    }

    private int ViewportHeight()
    {
        _screen.RefreshSize();
        int bottom = 1 + ComputeMenuHeight(_screen.Width) + ComputeInputHeight(_screen.Width);
        return Math.Max(3, _screen.Height - 2 - bottom);
    }

    private int MaxScroll()
    {
        int total = BuildTranscriptLines(_screen.Width).Count;
        return Math.Max(0, total - ViewportHeight());
    }

    private List<string> BuildTranscriptLines(int w)
    {
        var lines = new List<string>();
        List<Msg> snapshot;
        lock (_gate) snapshot = _messages.ToList();

        foreach (Msg msg in snapshot)
        {
            switch (msg.Role)
            {
                case Role.User:
                    lines.Add(Ansi.Fg(AxiomTheme.Gold) + Ansi.Bold + "You" + Ansi.Reset);
                    lines.Add(Ansi.Fg(AxiomTheme.Border) + new string('─', Math.Max(8, w)) + Ansi.Reset);
                    // User text plain (their typing); still wrap cleanly.
                    foreach (string wl in Wrap(msg.Text, w))
                        lines.Add(Ansi.Fg(AxiomTheme.TextPrimary) + wl + Ansi.Reset);
                    lines.Add(string.Empty);
                    break;
                case Role.Assistant:
                    lines.Add(Ansi.Fg(AxiomTheme.Builder) + Ansi.Bold + "Axiom" + Ansi.Reset);
                    lines.Add(Ansi.Fg(AxiomTheme.Border) + new string('─', Math.Max(8, w)) + Ansi.Reset);
                    // Markdown rendering: **bold**, *italic*, `code`, fences, lists, headers, $math$.
                    lines.AddRange(MarkdownAnsi.RenderLines(msg.Text, w, AxiomTheme.TextPrimary));
                    lines.Add(string.Empty);
                    break;
                case Role.Status:
                    lines.Add(Ansi.Fg(AxiomTheme.Success) + "● " + Ansi.Fg(AxiomTheme.SystemMuted) + msg.Text + Ansi.Reset);
                    lines.Add(string.Empty);
                    break;
                case Role.Error:
                    lines.Add(Ansi.Fg(AxiomTheme.Error) + "Error: " + msg.Text + Ansi.Reset);
                    lines.Add(string.Empty);
                    break;
                default:
                    // System / Architect / Critic dumps — light markdown so ** and ` still work.
                    lines.AddRange(MarkdownAnsi.RenderLines(msg.Text, w, AxiomTheme.SystemMuted));
                    lines.Add(string.Empty);
                    break;
            }
        }

        return lines;
    }

    private void PushUser(string text) { lock (_gate) _messages.Add(new Msg { Role = Role.User, Text = text }); }
    private void PushAssistant(string text) { lock (_gate) _messages.Add(new Msg { Role = Role.Assistant, Text = text }); }
    private void PushSystem(string text) { lock (_gate) _messages.Add(new Msg { Role = Role.System, Text = text }); }
    private void PushStatus(string text) { lock (_gate) _messages.Add(new Msg { Role = Role.Status, Text = text }); }
    private void PushError(string text) { lock (_gate) _messages.Add(new Msg { Role = Role.Error, Text = text }); }

    // ── Menu helpers (reuse ChatInput item builders) ─────────────

    private enum MenuMode { None, Slash, At }

    private MenuMode GetMenuMode(string text, int cursor)
    {
        if (string.IsNullOrEmpty(text))
            return MenuMode.None;
        if (text.StartsWith('/') && !text.Contains('\n'))
        {
            if (!text.Contains(' ') || IsKnownSlashHead(text.Split(' ', 2)[0]))
                return MenuMode.Slash;
        }
        if (TryGetAtToken(text, cursor, out _, out _, out _))
            return MenuMode.At;
        return MenuMode.None;
    }

    private static bool IsKnownSlashHead(string head)
    {
        head = head.ToLowerInvariant();
        return head is "/" or "/tools" or "/model" or "/clear" or "/help" or "/workspace" or "/ws"
            or "/sessions" or "/session" or "/browse" or "/folder" or "/open"
            or "/delete" or "/del" or "/rm" or "/undo" or "/mode" or "/resume"
            or "/continue" or "/cont" or "/rename" or "/export" or "/pick" or "/picker" or "/palette"
            or "/checkpoint" or "/cp" or "/plan" or "/changes" or "/accept" or "/reject"
            or "/replay" or "/jobs" or "/watch" or "/sticky" or "/pr" or "/network" or "/offline" or "/policy"
            or "/spec" or "/map" or "/council"
            || head.StartsWith("/t") || head.StartsWith("/m") || head.StartsWith("/c")
            || head.StartsWith("/h") || head.StartsWith("/e") || head.StartsWith("/s")
            || head.StartsWith("/w") || head.StartsWith("/b") || head.StartsWith("/f")
            || head.StartsWith("/o") || head.StartsWith("/d") || head.StartsWith("/r")
            || head.StartsWith("/u") || head.StartsWith("/p") || head.StartsWith("/e")
            || head.StartsWith("/a") || head.StartsWith("/j") || head.StartsWith("/g");
    }

    private IReadOnlyList<ChatInput.MenuItem> GetMenuItems(MenuMode mode)
    {
        if (_session == null)
            return Array.Empty<ChatInput.MenuItem>();

        if (mode == MenuMode.Slash)
        {
            string filter = _input.StartsWith('/') ? _input[1..] : _input;
            if (filter.StartsWith("model ", StringComparison.OrdinalIgnoreCase))
                filter = filter["model ".Length..];
            else if (filter.StartsWith("tools", StringComparison.OrdinalIgnoreCase))
                filter = filter.Length > 5 ? filter[5..].TrimStart() : string.Empty;
            var all = ChatInput.BuildSlashItems(_session.Tools, _models, _sessionStore.List(max: 12));
            return FilterItems(all, filter);
        }

        if (mode == MenuMode.At && TryGetAtToken(_input, _cursor, out _, out _, out string token))
        {
            string filter = token.StartsWith('@') ? token[1..] : token;
            var all = ChatInput.BuildFolderItems(_session.Workspace.Recent.GetRecent());
            return FilterItems(all, filter);
        }

        return Array.Empty<ChatInput.MenuItem>();
    }

    private static IReadOnlyList<ChatInput.MenuItem> FilterItems(IReadOnlyList<ChatInput.MenuItem> all, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return all;
        return all.Where(i =>
            i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || i.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || i.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Returns a command to submit immediately, or null to stay in the editor.
    private string? ApplyMenuSelection(ChatInput.MenuItem pick, MenuMode mode)
    {
        if (_session == null)
            return null;

        if (mode == MenuMode.At || pick.Kind == "folder")
        {
            if (TryGetAtToken(_input, _cursor, out int start, out int end, out _))
            {
                _input = _input.Remove(start, end - start);
                _cursor = start;
            }

            // Browse… opens the native OS folder dialog (Explorer / Finder / zenity).
            if (pick.Id == "__browse__")
            {
                PickAndLockFolder();
                return null;
            }

            // Explicit folder choice locks the agent exclusively to that tree.
            if (_session.Workspace.TrySetExclusive(pick.Id))
            {
                PushSystem(DescribeWorkspaceLock(pick.Id));
                AutoSave();
            }
            else
            {
                PushError($"Could not lock workspace: {pick.Id}");
            }
            return null;
        }

        if (pick.IsTool)
        {
            _session.Tools.TryToggle(pick.Id, out bool on);
            PushSystem($"{pick.Id} → {(on ? "on" : "off")}");
            _input = "/";
            _cursor = 1;
            return null;
        }

        if (pick.Id == "exit")
        {
            _running = false;
            return null;
        }

        // Commands that should run immediately (this was the /help bug: it only filled the buffer).
        if (pick.Id is "clear" or "help" or "workspace" or "sessions" or "browse" or "delete" or "undo" or "mode"
            or "continue" or "export" or "pick")
            return pick.Id == "mode" ? "/mode" : pick.Id == "rename" ? "/rename " : "/" + pick.Id;
        if (pick.Id == "rename")
            return "/rename ";

        if (pick.Id.StartsWith("session-del:", StringComparison.Ordinal))
        {
            string key = pick.Id["session-del:".Length..];
            return key.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? "/delete all"
                : "/delete " + key;
        }

        if (pick.Id.StartsWith("model:", StringComparison.Ordinal))
            return "/model " + pick.Id["model:".Length..];

        return null;
    }

    public void BrowseWorkspaceFolder() => PickAndLockFolder();

    private void PickAndLockFolder()
    {
        if (_session == null)
            return;

        // Leave alt-screen briefly so native dialogs paint correctly (esp. Windows Forms).
        _screen.Leave();
        string? path = null;
        try
        {
            path = NativeFolderPicker.PickFolder(_session.Workspace.PrimaryRoot);
        }
        finally
        {
            _screen.Enter();
            Paint(force: true);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            PushSystem("Folder picker cancelled.");
            return;
        }

        if (_session.Workspace.TrySetExclusive(path))
        {
            PushSystem(DescribeWorkspaceLock(path));
            AutoSave();
        }
        else
        {
            PushError($"Could not lock workspace: {path}");
        }
    }

    private static string DescribeWorkspaceLock(string path)
    {
        try
        {
            var access = new WorkspaceAccessService();
            WorkspaceIndexResult index = access.IndexWorkspace(path);
            return $"Workspace locked to: {path} · {index.Files.Count} readable file(s) indexed — the model can see this folder.";
        }
        catch
        {
            return $"Workspace locked to: {path} — the model can work inside this folder.";
        }
    }

    private static string ShortName(string path)
    {
        try
        {
            string n = new System.IO.DirectoryInfo(path).Name;
            return string.IsNullOrWhiteSpace(n) ? path : n;
        }
        catch { return path; }
    }

    private void RemoveAtToken()
    {
        if (TryGetAtToken(_input, _cursor, out int start, out int end, out _))
        {
            _input = _input.Remove(start, end - start);
            _cursor = start;
        }
    }

    private static bool TryGetAtToken(string text, int cursor, out int start, out int end, out string token)
    {
        start = end = 0;
        token = string.Empty;
        int i = Math.Clamp(cursor, 0, text.Length);
        int at = -1;
        for (int p = i - 1; p >= 0; p--)
        {
            char c = text[p];
            if (c == '@')
            {
                if (p == 0 || char.IsWhiteSpace(text[p - 1])) { at = p; break; }
                return false;
            }
            if (char.IsWhiteSpace(c))
                return false;
        }
        if (at < 0) return false;
        end = at + 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        if (cursor < at || cursor > end) return false;
        start = at;
        token = text[at..end];
        return true;
    }

    private static List<string> Wrap(string text, int width)
    {
        width = Math.Max(8, width);
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(string.Empty);
            return result;
        }

        foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0) { result.Add(string.Empty); continue; }
            int i = 0;
            while (i < paragraph.Length)
            {
                int take = Math.Min(width, paragraph.Length - i);
                if (take < paragraph.Length - i)
                {
                    int sp = paragraph.LastIndexOf(' ', i + take - 1, take);
                    if (sp >= i) take = Math.Max(1, sp - i + 1);
                }
                result.Add(paragraph.Substring(i, take).TrimEnd());
                i += take;
            }
        }
        return result;
    }

    private static (int row, int col) CursorToRowCol(string text, int cursor, int inner)
    {
        if (string.IsNullOrEmpty(text) || cursor <= 0)
            return (0, 0);
        int row = 0, col = 0;
        for (int i = 0; i < text.Length && i < cursor; i++)
        {
            if (text[i] == '\n') { row++; col = 0; continue; }
            col++;
            if (col >= inner) { row++; col = 0; }
        }
        return (row, col);
    }

    private static string FitColored(string s, int w)
    {
        int vis = Ansi.VisibleLength(s);
        if (vis < w) return s + new string(' ', w - vis);
        return s;
    }

    private static string GetVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
