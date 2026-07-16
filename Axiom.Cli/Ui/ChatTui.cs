using System;
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
using Axiom.Core.Tools;
using Axiom.Core.Workspace;

namespace Axiom.Cli.Ui;

// Self-contained chat interface: alternate-screen full window with
//   [ header ]
//   [ scrollable message viewport  ← app-managed, not host scrollbar ]
//   [ activity line ]
//   [ fixed prompt box + model line ]
//
// PgUp/PgDn / Ctrl+↑↓ / mouse wheel scroll the transcript. The prompt stays pinned.
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

    public bool DeleteSession(string idOrPrefix) => _sessionStore.Delete(idOrPrefix);

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

        // First-run: popup to paste OpenRouter API key (Enter to submit).
        if (!session.ChatService.HasValidKey)
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
        }

        PushSystem($"Axiom ready · {session.ModelLabel} · council {(session.Tools.CouncilEnabled ? "on" : "off")} · /help · @ or /browse for folders");

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

            if (_busy)
            {
                Interlocked.Increment(ref _animFrame);
                Paint();
            }
        }
    }

    private async Task<bool> HandleKeyAsync(ConsoleKeyInfo key)
    {
        // Mouse wheel (SGR / legacy) arrives as special keys on some hosts — treat as scroll when possible.
        if (key.Key == ConsoleKey.PageUp || (key.Key == ConsoleKey.UpArrow && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
        {
            _scrollFromBottom = Math.Min(_scrollFromBottom + Math.Max(3, ViewportHeight() / 3), MaxScroll());
            return true;
        }
        if (key.Key == ConsoleKey.PageDown || (key.Key == ConsoleKey.DownArrow && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
        {
            _scrollFromBottom = Math.Max(0, _scrollFromBottom - Math.Max(3, ViewportHeight() / 3));
            return true;
        }
        if (key.Key == ConsoleKey.Home && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            _scrollFromBottom = MaxScroll();
            return true;
        }
        if (key.Key == ConsoleKey.End && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            _scrollFromBottom = 0;
            return true;
        }

        if (_busy)
            return false; // ignore typing while generating (scroll still handled above)

        // Slash / @ menu navigation
        MenuMode mode = GetMenuMode(_input, _cursor);
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
        _attachPaths?.Invoke(input, _session);
        _scrollFromBottom = 0;
        Paint(force: true);

        _busy = true;
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
                await RunCouncilTurnAsync(grounded, sw);
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
                    CancellationToken.None);

                await PumpScrollWhileAsync(agentTask);

                AgentTurnResult result = await agentTask;
                toolCalls = result.ToolCallCount;
                sw.Stop();

                if (assistantIndex < 0 && !string.IsNullOrEmpty(result.ResponseText))
                    PushAssistant(result.ResponseText);
                else if (assistantIndex >= 0)
                    lock (_gate) _messages[assistantIndex].Text = result.ResponseText ?? collected.ToString();

                PushStatus(ActivityStatus.SummarizeTurn(result.Elapsed, result.ToolCallCount, result.Failed));
            }

            _turnCount++;
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
            _scrollFromBottom = 0;
            Paint(force: true);
        }
    }

    // Architect → Builder → Critic pipeline (mirrors desktop Workplace council control flow).
    private async Task RunCouncilTurnAsync(string grounded, Stopwatch sw)
    {
        if (_session == null)
            return;

        SetActivity("Council · Architect planning");
        Paint();

        var progress = new Progress<CouncilEvent>(evt =>
        {
            switch (evt.Kind)
            {
                case CouncilEventKind.Status:
                case CouncilEventKind.Warning:
                    SetActivity(evt.Message);
                    break;
                case CouncilEventKind.ArchitectOutput:
                    SetActivity("Council · Architect done");
                    PushSystem("Architect plan\n" + Truncate(evt.Message, 1200));
                    break;
                case CouncilEventKind.BuilderOutput:
                    SetActivity("Council · Builder working");
                    break;
                case CouncilEventKind.CriticOutput:
                    SetActivity("Council · Critic reviewing");
                    PushSystem("Critic review\n" + Truncate(evt.Message, 800));
                    break;
                case CouncilEventKind.Completed:
                    SetActivity("Council · Finished");
                    break;
                case CouncilEventKind.Failed:
                    SetActivity("Council · Failed");
                    break;
            }
            Paint();
        });

        ConnectedWorkspaceState? wsState = null;
        if (_session.Workspace.Roots.Count > 0)
        {
            try
            {
                // Full folder connection (index + ConnectionKind=Folder) so council never sees "None".
                var access = new WorkspaceAccessService();
                wsState = access.CreateFolderConnection(_session.Workspace.PrimaryRoot);
                SetActivity($"Workspace · {wsState.IndexedFileCount} file(s) indexed");
                Paint();
            }
            catch (Exception ex)
            {
                // Still attach RootPath so access language can fire even if indexing throws.
                wsState = new ConnectedWorkspaceState
                {
                    CodebaseEditAccessEnabled = true,
                    ConnectionKind = WorkspaceConnectionKind.Folder.ToString(),
                    RootPath = _session.Workspace.PrimaryRoot,
                    StatusMessage = "Folder connected (index incomplete: " + ex.Message + ")"
                };
            }
        }

        CouncilOrchestrator council = _session.CreateCouncil();
        Task<CouncilResult> task = council.RunAsync(
            new CouncilRequest(grounded, wsState, _session.CouncilTools()),
            progress,
            CancellationToken.None);

        await PumpScrollWhileAsync(task);
        CouncilResult result = await task;
        sw.Stop();

        string final = result.FinalText ?? string.Empty;
        if (result.Patch != null)
        {
            // Surface patch text for the user; applying still goes through axiom code for review,
            // but chat council can still propose structured output.
            final = string.IsNullOrWhiteSpace(final) ? result.Patch.RawText : final;
        }

        PushAssistant(string.IsNullOrWhiteSpace(final) ? "(Council produced no text.)" : final);
        _session.History.Add(new OpenRouterMessage("user", grounded));
        _session.History.Add(new OpenRouterMessage("assistant", final));

        string summary = result.Success
            ? ActivityStatus.SummarizeTurn(sw.Elapsed, 3) // three roles
            : ActivityStatus.SummarizeTurn(sw.Elapsed, 3, failed: true);
        if (result.FinalCriticReport != null && result.FinalCriticReport.HasIssues)
            summary += $" · Critic: {result.FinalCriticReport.FindingsCount} issue(s)";
        PushStatus("Council · " + summary);
    }

    private async Task PumpScrollWhileAsync(Task task)
    {
        while (!task.IsCompleted)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.PageUp || (k.Key == ConsoleKey.UpArrow && k.Modifiers.HasFlag(ConsoleModifiers.Control)))
                    _scrollFromBottom = Math.Min(_scrollFromBottom + Math.Max(3, ViewportHeight() / 3), MaxScroll());
                else if (k.Key == ConsoleKey.PageDown || (k.Key == ConsoleKey.DownArrow && k.Modifiers.HasFlag(ConsoleModifiers.Control)))
                    _scrollFromBottom = Math.Max(0, _scrollFromBottom - Math.Max(3, ViewportHeight() / 3));
                else if (k.Key == ConsoleKey.End && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                    _scrollFromBottom = 0;
                Paint();
            }
            await Task.Delay(20);
        }
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
                Title = SessionStore.MakeTitle(firstUser),
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

        // ── Header ──────────────────────────────────────────────
        var session = _session;
        (int used, int max) = session?.EstimateContext() ?? (0, 0);
        string tools = session != null ? ConsoleUi.ToolChipsPlain(session.Tools) : "";
        string model = session?.ModelLabel ?? "—";
        string ctx = ConsoleUi.FormatContext(used, max);
        // Minimal mark top-left (◆) — small, non-disruptive brand anchor.
        string mark = "◆";
        string wsHint = session?.Workspace.Roots.Count > 0
            ? ShortName(session.Workspace.PrimaryRoot)
            : "no-folder";
        string left = $" {mark} Axiom  v{GetVersion()}  ·  {model}  ·  {wsHint}";
        string header = ConsoleUi.LayoutThree(left, tools, $"{ctx} ", w);
        rows[0] = Ansi.Fg(AxiomTheme.Gold) + Ansi.Bold + mark + Ansi.Reset
            + Ansi.Fg(AxiomTheme.TextPrimary) + Ansi.ClipPad(header.Length > 1 ? header[1..] : header, Math.Max(1, w - 1)) + Ansi.Reset;
        // Re-paint full header line cleanly (mark already included in left).
        rows[0] = Ansi.Fg(AxiomTheme.Gold) + Ansi.Bold + Ansi.ClipPad(header, w) + Ansi.Reset;
        rows[1] = Ansi.Fg(AxiomTheme.Border) + new string('═', w) + Ansi.Reset;

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
            string hint = $" ↑ {_scrollFromBottom} more · PgDn / Ctrl+↓ ";
            int row = headerH + viewportH - 1;
            rows[row] = Ansi.Fg(AxiomTheme.Gold) + Ansi.ClipPad(hint.PadLeft((w + hint.Length) / 2).PadRight(w), w) + Ansi.Reset;
        }

        // ── Activity ────────────────────────────────────────────
        int activityRow = headerH + viewportH;
        if (_busy && !string.IsNullOrEmpty(_activity))
        {
            string glyph = _animFrames[Math.Abs(_animFrame) % _animFrames.Length];
            string act = $" {glyph}  {_activity}";
            rows[activityRow] = Ansi.Fg(AxiomTheme.Gold) + Ansi.ClipPad(act, w) + Ansi.Reset;
        }
        else
        {
            string tip = " PgUp/PgDn scroll transcript  ·  / tools  ·  @ folders  ·  exit ";
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

    private int inputContentRows(int w) => 2;

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
                    foreach (string wl in Wrap(msg.Text, w))
                        lines.Add(Ansi.Fg(AxiomTheme.TextPrimary) + wl + Ansi.Reset);
                    lines.Add(string.Empty);
                    break;
                case Role.Assistant:
                    lines.Add(Ansi.Fg(AxiomTheme.Builder) + Ansi.Bold + "Axiom" + Ansi.Reset);
                    lines.Add(Ansi.Fg(AxiomTheme.Border) + new string('─', Math.Max(8, w)) + Ansi.Reset);
                    foreach (string wl in Wrap(msg.Text, w))
                        lines.Add(Ansi.Fg(AxiomTheme.TextPrimary) + wl + Ansi.Reset);
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
                    foreach (string wl in Wrap(msg.Text, w))
                        lines.Add(Ansi.Fg(AxiomTheme.SystemMuted) + wl + Ansi.Reset);
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
            || head.StartsWith("/t") || head.StartsWith("/m") || head.StartsWith("/c")
            || head.StartsWith("/h") || head.StartsWith("/e") || head.StartsWith("/s")
            || head.StartsWith("/w") || head.StartsWith("/b") || head.StartsWith("/f")
            || head.StartsWith("/o");
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
            var all = ChatInput.BuildSlashItems(_session.Tools, _models);
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
        if (pick.Id is "clear" or "help" or "workspace" or "sessions" or "browse")
            return "/" + pick.Id;

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
