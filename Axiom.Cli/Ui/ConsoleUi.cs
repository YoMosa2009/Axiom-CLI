using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Full-width chat chrome: header spans the window, messages stack like a GUI chat,
// and the input box is a dedicated wide panel with the active model on the seam below.
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

    public static int SafeWidth()
    {
        try { return Math.Max(60, Console.WindowWidth); }
        catch { return 100; }
    }

    public static void ConfigureConsole()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { /* some hosts lock encoding */ }

        try { Console.Out.Flush(); } catch { }
    }

    public static void ShowWelcome(
        string modelLabel,
        SessionToolSettings tools,
        int usedTokens,
        int contextWindowTokens,
        IReadOnlyList<string>? workspaces = null)
    {
        WriteHeader(modelLabel, tools, usedTokens, contextWindowTokens, workspaces);
        int width = SafeWidth();
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string secondary = AxiomTheme.Hex(AxiomTheme.TextSecondary);

        AnsiConsole.MarkupLine(
            $"[{secondary}]Full-width chat · type below · [/][{gold}]/[/][{secondary}] tools  [/][{gold}]@[/][{secondary}] folders  [/][{muted}]exit[/][{secondary}] to quit[/]");
        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, width - 1))}[/]");
        AnsiConsole.WriteLine();
    }

    public static void WriteHeader(
        string modelLabel,
        SessionToolSettings tools,
        int usedTokens,
        int contextWindowTokens,
        IReadOnlyList<string>? workspaces = null)
    {
        int width = SafeWidth();
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);

        string brand = $"Axiom  v{Version}";
        string mid = $"{modelLabel}  ·  {ToolChipsPlain(tools)}";
        string context = FormatContext(usedTokens, contextWindowTokens);

        // Three-zone full-width header: brand | tools | context (right-aligned).
        string header = LayoutThree(brand, mid, context, width - 1);
        AnsiConsole.WriteLine();
        try
        {
            AnsiConsole.Markup($"[bold {gold}]{brand.EscapeMarkup()}[/]");
            string rest = header.Length > brand.Length ? header[brand.Length..] : string.Empty;
            // Highlight context on the right by rewriting: find last context occurrence.
            int ctxIdx = rest.LastIndexOf(context, StringComparison.Ordinal);
            if (ctxIdx >= 0)
            {
                AnsiConsole.Markup($"[{muted}]{rest[..ctxIdx].EscapeMarkup()}[/]");
                AnsiConsole.Markup($"[{primary}]{context.EscapeMarkup()}[/]");
            }
            else
            {
                AnsiConsole.Markup($"[{muted}]{rest.EscapeMarkup()}[/]");
            }
            Console.WriteLine();
        }
        catch
        {
            Console.WriteLine(header);
        }

        AnsiConsole.MarkupLine($"[{muted}]{new string('═', Math.Max(20, width - 1))}[/]");

        if (workspaces is { Count: > 0 })
        {
            string roots = string.Join("   ", workspaces.Take(4).Select(w => ShortPath(w)));
            if (workspaces.Count > 4)
                roots += $"  +{workspaces.Count - 4} more";
            string wsLine = LayoutThree("workspace", roots, "", width - 1);
            AnsiConsole.MarkupLine($"[{muted}]{wsLine.EscapeMarkup()}[/]");
        }
    }

    public static void WriteUserMessage(string text)
    {
        int width = SafeWidth();
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold {gold}]You[/]");
        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, width - 1))}[/]");
        WriteWrapped(text ?? string.Empty, primary);
        AnsiConsole.WriteLine();
    }

    public static void WriteAssistantHeader()
    {
        int width = SafeWidth();
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string builder = AxiomTheme.Hex(AxiomTheme.Builder);

        AnsiConsole.MarkupLine($"[bold {builder}]Axiom[/]");
        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, width - 1))}[/]");
    }

    public static void WriteTurnSummary(string statusLine)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string success = AxiomTheme.Hex(AxiomTheme.Success);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{success}]●[/] [{muted}]{statusLine.EscapeMarkup()}[/]");
    }

    public static void WriteActivityLine(string status)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        try
        {
            int width = Math.Max(20, Console.WindowWidth - 1);
            string text = $"  {status}";
            if (text.Length > width)
                text = text[..(width - 1)] + "…";
            // Live single-line activity (full width).
            Console.Write("\r");
            AnsiConsole.Markup($"[{gold}]◉[/] [{muted}]{text.EscapeMarkup()}[/]");
            int pad = Math.Max(0, width - VisibleLen(status) - 4);
            if (pad > 0)
                Console.Write(new string(' ', pad));
        }
        catch
        {
            AnsiConsole.MarkupLine($"[{muted}]{status.EscapeMarkup()}[/]");
        }
    }

    public static void ClearActivityLine()
    {
        try
        {
            int width = Math.Max(20, Console.WindowWidth - 1);
            Console.Write("\r" + new string(' ', width) + "\r");
        }
        catch { /* ignore */ }
    }

    public static string FormatContext(int usedTokens, int contextWindowTokens)
    {
        return $"{FormatTokenCount(usedTokens)} / {FormatTokenCount(contextWindowTokens)}";
    }

    public static string FormatTokenCount(int tokens)
    {
        if (tokens >= 1_000_000)
            return $"{tokens / 1_000_000.0:0.0}M";
        if (tokens >= 10_000)
            return $"{tokens / 1000.0:0.0}k";
        if (tokens >= 1000)
            return $"{tokens / 1000.0:0.00}k";
        return tokens.ToString();
    }

    public static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s";
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        if (elapsed.TotalSeconds >= 10)
            return $"{elapsed.TotalSeconds:0}s";
        return $"{elapsed.TotalSeconds:0.0}s";
    }

    public static void ShowToolsPanel(SessionToolSettings tools)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);
        string success = AxiomTheme.Hex(AxiomTheme.Success);
        int width = SafeWidth();

        AnsiConsole.MarkupLine($"[{muted}]{new string('─', width - 1)}[/]");
        foreach ((string name, bool enabled, string description) in ToolDescriptions(tools))
        {
            string status = enabled ? $"[{success}]● on [/]" : $"[{muted}]○ off[/]";
            AnsiConsole.MarkupLine($"  [{primary}]{name,-14}[/] {status}  [{muted}]{description.EscapeMarkup()}[/]");
        }
        AnsiConsole.MarkupLine($"[{muted}]Tip: type / then ↑↓ + Enter to toggle · {new string('─', 8)}[/]");
    }

    private static (string Name, bool Enabled, string Description)[] ToolDescriptions(SessionToolSettings tools) =>
    [
        ("calculator", tools.CalculatorEnabled, "Math expressions and unit conversions"),
        ("web-search", tools.WebSearchEnabled, "Live information lookup"),
        ("sandbox", tools.SandboxEnabled, "Local Python execution (off by default)")
    ];

    public static string ToolChipsPlain(SessionToolSettings tools)
        => string.Join("  ", tools.AsList().Select(t => (t.Enabled ? "● " : "○ ") + t.Name));

    public static void ShowModelPanel(string currentModelLabel, (string Id, string Label, string Description)[] models)
    {
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);
        string secondary = AxiomTheme.Hex(AxiomTheme.TextSecondary);

        foreach ((string id, string label, string description) in models)
        {
            bool active = string.Equals(label, currentModelLabel, StringComparison.OrdinalIgnoreCase);
            string name = active ? $"[{gold}]● {label}[/]" : $"[{primary}]○ {label}[/]";
            AnsiConsole.MarkupLine($"  {name}  [{secondary}]{description.EscapeMarkup()}[/]  [{muted}]({id})[/]");
        }
    }

    public static void StatusFooter(
        string modelLabel,
        SessionToolSettings tools,
        int turnCount,
        int usedTokens,
        int contextWindowTokens)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        int width = SafeWidth();
        string context = FormatContext(usedTokens, contextWindowTokens);
        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, width - 1))}[/]");
        string line = LayoutThree(
            $"{modelLabel}  ·  turn {turnCount}",
            ToolChipsPlain(tools),
            context,
            width - 1);
        AnsiConsole.MarkupLine($"[{muted}]{line.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
    }

    // Full-width three-column line: left | center | right (right is right-aligned).
    public static string LayoutThree(string left, string center, string right, int width)
    {
        width = Math.Max(40, width);
        left ??= string.Empty;
        center ??= string.Empty;
        right ??= string.Empty;

        // Prefer keeping left + right; fill middle with center when space allows.
        if (left.Length + right.Length + 2 >= width)
        {
            int keep = Math.Max(8, width - right.Length - 4);
            if (left.Length > keep)
                left = left[..(keep - 1)] + "…";
            int gap = Math.Max(1, width - left.Length - right.Length);
            return left + new string(' ', gap) + right;
        }

        int remaining = width - left.Length - right.Length;
        if (center.Length + 4 > remaining)
        {
            int gap = Math.Max(2, remaining);
            return left + new string(' ', gap) + right;
        }

        // Distribute remaining space so center is roughly mid, right hugs the edge.
        int sidePad = Math.Max(2, (remaining - center.Length) / 2);
        int rightPad = Math.Max(2, remaining - center.Length - sidePad);
        return left + new string(' ', sidePad) + center + new string(' ', rightPad) + right;
    }

    private static void WriteWrapped(string text, string colorHex)
    {
        int width = Math.Max(40, SafeWidth() - 2);
        foreach (string paragraph in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                AnsiConsole.WriteLine();
                continue;
            }

            int i = 0;
            while (i < paragraph.Length)
            {
                int take = Math.Min(width, paragraph.Length - i);
                // Prefer breaking on space when possible.
                if (take < paragraph.Length - i)
                {
                    int sp = paragraph.LastIndexOf(' ', i + take - 1, take);
                    if (sp > i)
                        take = sp - i + 1;
                }
                string chunk = paragraph.Substring(i, take).TrimEnd();
                AnsiConsole.MarkupLine($"[{colorHex}]{chunk.EscapeMarkup()}[/]");
                i += take;
            }
        }
    }

    private static string ShortPath(string path)
    {
        try
        {
            string name = new System.IO.DirectoryInfo(path).Name;
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch { return path; }
    }

    private static int VisibleLen(string s) => s?.Length ?? 0;

    // Live activity line with status-specific animations (spinner, bars, orbit, arrows, …).
    public sealed class ThinkingIndicator : IDisposable
    {
        private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        private static readonly string[] Orbit = ["◐", "◓", "◑", "◒"];
        private static readonly string[] Bounce = ["⠁", "⠂", "⠄", "⡀", "⢀", "⠠", "⠐", "⠈"];
        private static readonly string[] PulseBar = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█", "▇", "▆", "▅", "▄", "▃", "▂"];
        private static readonly string[] Blocks = ["▓░░░", "░▓░░", "░░▓░", "░░░▓", "░░▓░", "░▓░░"];
        private static readonly string[] Dots = ["·   ", "··  ", "··· ", "····", " ···", "  ··", "   ·"];
        private static readonly string[] Arrows = ["→   ", " →  ", "  → ", "   →", "  → ", " →  "];
        private static readonly string[] Sparkle = ["✧", "✦", "✶", "✦"];
        private static readonly string[] Wave = ["⋆ ⋆ ⋆", " ⋆ ⋆ ", "⋆ ⋆ ⋆", " ⋆ ⋆ "];
        private static readonly string[] Moon = ["○", "◔", "◑", "◕", "●", "◕", "◑", "◔"];
        private static readonly string[] CheckPulse = ["✓  ", " ✓ ", "  ✓", " ✓ "];
        private static readonly string[] ErrorPulse = ["✕  ", " ✕ ", "  ✕", " ✕ "];

        private readonly CancellationTokenSource _cts = new();
        private readonly Task? _task;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _gate = new();
        private string _label;
        private string[] _frames;
        private int _delayMs;
        private int _stopped;
        private int _lastLen;

        public ThinkingIndicator(string label = ActivityStatus.Thinking)
        {
            _label = label;
            (_frames, _delayMs) = ResolveAnimation(label);
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
                        string glyph;
                        string labelCopy;
                        int delay;
                        lock (_gate)
                        {
                            glyph = _frames[frame % _frames.Length];
                            labelCopy = _label;
                            delay = _delayMs;
                        }
                        Paint($"{glyph}  {labelCopy}  ·  {seconds:0.0}s");
                        frame++;
                        await Task.Delay(delay, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public void SetLabel(string label)
        {
            lock (_gate)
            {
                _label = label;
                (_frames, _delayMs) = ResolveAnimation(label);
            }
        }

        private static (string[] Frames, int DelayMs) ResolveAnimation(string label)
        {
            string l = (label ?? string.Empty).ToLowerInvariant();

            if (l.Contains("fail") || l.Contains("error") || l.Contains("stopped") || l.Contains("cancel"))
                return (ErrorPulse, 140);
            if (l.Contains("complete") || l.Contains("finished") || l.Contains("done") || l.Contains("success"))
                return (CheckPulse, 160);
            if (l.Contains("download"))
                return (Arrows, 90);
            if (l.Contains("search") || l.Contains("look") || l.Contains("find"))
                return (Dots, 100);
            if (l.Contains("build") || l.Contains("compil") || l.Contains("install") || l.Contains("running") || l.Contains("command"))
                return (PulseBar, 70);
            if (l.Contains("writ") || l.Contains("read") || l.Contains("list"))
                return (Blocks, 95);
            if (l.Contains("generat") || l.Contains("stream") || l.Contains("respond"))
                return (Orbit, 110);
            if (l.Contains("plan") || l.Contains("review"))
                return (Moon, 130);
            if (l.Contains("connect") || l.Contains("wait") || l.Contains("retry"))
                return (Wave, 120);
            if (l.Contains("work") || l.Contains("step") || l.Contains("tool"))
                return (Sparkle, 100);
            if (l.Contains("think"))
                return (Spinner, 80);

            // Default: gentle bounce
            return (Bounce, 90);
        }

        private void Paint(string text)
        {
            lock (_gate)
            {
                if (_stopped != 0 || Console.IsOutputRedirected)
                    return;
                try
                {
                    int width = Math.Max(40, Console.WindowWidth - 1);
                    if (text.Length > width)
                        text = text[..(width - 1)] + "…";
                    int pad = Math.Max(_lastLen, text.Length);
                    Console.Write("\r" + text.PadRight(pad));
                    _lastLen = text.Length;
                }
                catch { }
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            _cts.Cancel();
            try { _task?.Wait(300); } catch { }
            lock (_gate)
            {
                if (Console.IsOutputRedirected)
                    return;
                try
                {
                    int pad = Math.Max(_lastLen, 1);
                    Console.Write("\r" + new string(' ', Math.Max(pad, Console.WindowWidth - 1)) + "\r");
                    _lastLen = 0;
                }
                catch { }
            }
        }

        public void Dispose() => Stop();
    }

    public sealed class TokenStream : IDisposable
    {
        private readonly StringBuilder _buf = new();
        private readonly object _gate = new();
        private bool _headerWritten;
        private bool _completed;

        public void WriteToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return;

            lock (_gate)
            {
                if (_completed)
                    return;
                if (!_headerWritten)
                {
                    WriteAssistantHeader();
                    _headerWritten = true;
                }
                _buf.Append(token);
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                    return;
                _completed = true;

                if (_buf.Length > 0)
                {
                    if (!_headerWritten)
                    {
                        WriteAssistantHeader();
                        _headerWritten = true;
                    }
                    LinkText.WriteWithLinks(_buf.ToString());
                    _buf.Clear();
                    Console.WriteLine();
                }
            }
        }

        public void Dispose() => Complete();
    }
}
