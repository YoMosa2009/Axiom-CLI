using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Full-width, low-chrome terminal chrome — closer to Claude Code / Codex than a boxed panel UI.
internal static class ConsoleUi
{
    private static string Version
    {
        get
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static void ConfigureConsole()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { /* some hosts lock encoding */ }

        // Disable out auto-flush thrash during token streaming on some Windows hosts.
        try { Console.Out.Flush(); } catch { }
    }

    public static void ShowWelcome(string modelLabel, SessionToolSettings tools)
    {
        int width = SafeWidth();
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);
        string secondary = AxiomTheme.Hex(AxiomTheme.TextSecondary);

        AnsiConsole.WriteLine();
        // Single compact brand line — full width, no left-biased panel.
        AnsiConsole.MarkupLine(
            $"[bold {gold}]Axiom[/]  [{muted}]v{Version}[/]  [{muted}]·[/]  [{primary}]{modelLabel.EscapeMarkup()}[/]  [{muted}]·[/]  {ToolChips(tools)}");
        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, width - 1))}[/]");
        AnsiConsole.MarkupLine(
            $"[{secondary}]Type a message to chat. Press [/][{gold}]/[/][{secondary}] for tools & commands. [/][{muted}]exit[/][{secondary}] to quit.[/]");
        AnsiConsole.WriteLine();
    }

    public static void ShowToolsPanel(SessionToolSettings tools)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);
        string success = AxiomTheme.Hex(AxiomTheme.Success);

        foreach ((string name, bool enabled, string description) in ToolDescriptions(tools))
        {
            string status = enabled
                ? $"[{success}]● on[/]"
                : $"[{muted}]○ off[/]";
            AnsiConsole.MarkupLine(
                $"  [{primary}]{name,-12}[/] {status}  [{muted}]{description.EscapeMarkup()}[/]");
        }
        AnsiConsole.MarkupLine($"[{muted}]Tip: type / then ↑↓ and Enter to toggle tools.[/]");
    }

    private static (string Name, bool Enabled, string Description)[] ToolDescriptions(SessionToolSettings tools) =>
    [
        ("calculator", tools.CalculatorEnabled, "Math expressions and unit conversions"),
        ("web-search", tools.WebSearchEnabled, "Live information lookup"),
        ("sandbox", tools.SandboxEnabled, "Local Python execution (off by default)")
    ];

    public static string ToolChips(SessionToolSettings tools)
    {
        string success = AxiomTheme.Hex(AxiomTheme.Success);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        return string.Join("  ", tools.AsList().Select(t =>
            t.Enabled
                ? $"[{success}]●[/] [{muted}]{t.Name}[/]"
                : $"[{muted}]○ {t.Name}[/]"));
    }

    public static void ShowModelPanel(string currentModelLabel, (string Id, string Label, string Description)[] models)
    {
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);
        string secondary = AxiomTheme.Hex(AxiomTheme.TextSecondary);

        foreach ((string id, string label, string description) in models)
        {
            bool active = string.Equals(label, currentModelLabel, StringComparison.OrdinalIgnoreCase);
            string name = active
                ? $"[{gold}]● {label}[/]"
                : $"[{primary}]○ {label}[/]";
            AnsiConsole.MarkupLine($"  {name}  [{secondary}]{description.EscapeMarkup()}[/]  [{muted}]({id})[/]");
        }
        AnsiConsole.MarkupLine($"[{muted}]Tip: type / then pick a model, or /model eidos|hepha[/]");
    }

    public static void StatusFooter(string modelLabel, SessionToolSettings tools, int turnCount)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        int width = SafeWidth();
        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, width - 1))}[/]");
        AnsiConsole.MarkupLine(
            $"[{muted}]{modelLabel.EscapeMarkup()}[/]  [{muted}]·[/]  [{muted}]turn {turnCount}[/]  [{muted}]·[/]  {ToolChips(tools)}");
    }

    public static void PromptDivider()
    {
        // Intentionally minimal — full-width divider only when needed for separation after errors.
    }

    public static void WriteUserEcho(string text)
    {
        // Input already visible on the prompt line; nothing extra required.
        _ = text;
    }

    public static void WriteAssistantPrefix()
    {
        AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]axiom[/]  ");
    }

    public static void WriteFullWidthRule()
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, SafeWidth() - 1))}[/]");
    }

    private static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch { return 100; }
    }

    // In-place spinner on a single console line. Uses raw Console.Write + \\r only — Spectre
    // Markup inside a \\r loop re-emits styling in ways that advance the cursor and "regenerate"
    // Thinking... lines down the screen.
    public sealed class ThinkingIndicator : IDisposable
    {
        private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        private readonly CancellationTokenSource _cts = new();
        private readonly Task? _task;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly string _label;
        private int _stopped;
        private int _lastLen;

        public ThinkingIndicator(string label = "Thinking")
        {
            _label = label;
            if (Console.IsOutputRedirected)
                return;

            _task = Task.Run(async () =>
            {
                int frame = 0;
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        double seconds = _stopwatch.Elapsed.TotalSeconds;
                        string text = $"{Frames[frame % Frames.Length]} {_label}... {seconds:0.0}s";
                        Paint(text);
                        frame++;
                        await Task.Delay(80, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on Stop().
                }
            });
        }

        private void Paint(string text)
        {
            lock (_gate)
            {
                if (_stopped != 0 || Console.IsOutputRedirected)
                    return;

                try
                {
                    int width = Math.Max(20, Console.WindowWidth - 1);
                    if (text.Length > width)
                        text = text[..width];

                    // Clear + rewrite the same line. Pad to previous length so leftover glyphs vanish.
                    int pad = Math.Max(_lastLen, text.Length);
                    Console.Write("\r" + text.PadRight(pad));
                    _lastLen = text.Length;
                }
                catch
                {
                    // Ignore transient console races during resize/shutdown.
                }
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            _cts.Cancel();
            try { _task?.Wait(300); } catch { /* best effort */ }

            lock (_gate)
            {
                if (Console.IsOutputRedirected)
                    return;
                try
                {
                    int pad = Math.Max(_lastLen, 1);
                    Console.Write("\r" + new string(' ', pad) + "\r");
                    _lastLen = 0;
                }
                catch { /* ignore */ }
            }
        }

        public void Dispose() => Stop();
    }

    // Batches streamed tokens so free-tier single-character deltas don't force a kernel write
    // per byte (which feels laggy and can interleave badly with other console activity).
    public sealed class TokenStream : IDisposable
    {
        private readonly StringBuilder _buf = new();
        private readonly object _gate = new();
        private readonly Stopwatch _sinceFlush = Stopwatch.StartNew();
        private bool _started;
        private bool _completed;

        public void WriteToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return;

            lock (_gate)
            {
                if (_completed)
                    return;

                if (!_started)
                {
                    WriteAssistantPrefix();
                    _started = true;
                }

                _buf.Append(token);
                // Flush on newline, after ~48 chars, or every 40ms — live enough without
                // one Write per tiny delta.
                if (token.Contains('\n') || _buf.Length >= 48 || _sinceFlush.ElapsedMilliseconds >= 40)
                    FlushUnlocked();
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                    return;
                _completed = true;
                FlushUnlocked();
                if (_started)
                    Console.WriteLine();
            }
        }

        private void FlushUnlocked()
        {
            if (_buf.Length == 0)
                return;
            Console.Write(_buf.ToString());
            _buf.Clear();
            _sinceFlush.Restart();
        }

        public void Dispose() => Complete();
    }
}
