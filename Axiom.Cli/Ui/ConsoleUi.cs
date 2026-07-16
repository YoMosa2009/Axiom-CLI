using System;
using System.Collections.Generic;
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
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string secondary = AxiomTheme.Hex(AxiomTheme.TextSecondary);

        AnsiConsole.MarkupLine(
            $"[{secondary}]Type a message. [/][{gold}]/[/][{secondary}] tools  [/][{gold}]@[/][{secondary}] folders  [/][{muted}]exit[/][{secondary}] quit[/]");
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

        string left = $"Axiom  v{Version}  ·  {modelLabel}  ·  {StripMarkup(ToolChipsPlain(tools))}";
        string context = FormatContext(usedTokens, contextWindowTokens);
        string line = BuildLeftRight(left, context, width);

        // Colorize via simple full-line muted + gold brand prefix.
        AnsiConsole.WriteLine();
        try
        {
            int ctxStart = line.LastIndexOf(context, StringComparison.Ordinal);
            if (ctxStart > 0)
            {
                string leftPart = line[..ctxStart];
                AnsiConsole.Markup($"[bold {gold}]Axiom[/][{muted}]{EscapeAfterBrand(leftPart)}[/]");
                AnsiConsole.Markup($"[{muted}]{context.EscapeMarkup()}[/]");
                Console.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine($"[{muted}]{line.EscapeMarkup()}[/]");
            }
        }
        catch
        {
            Console.WriteLine(line);
        }

        AnsiConsole.MarkupLine($"[{muted}]{new string('─', Math.Max(20, width - 1))}[/]");

        if (workspaces is { Count: > 0 })
        {
            string roots = string.Join("  ", workspaces.Take(3).Select(w => ShortPath(w)));
            if (workspaces.Count > 3)
                roots += $"  +{workspaces.Count - 3}";
            AnsiConsole.MarkupLine($"[{muted}]workspace[/]  [{primary}]{roots.EscapeMarkup()}[/]");
        }
    }

    private static string EscapeAfterBrand(string leftPart)
    {
        // leftPart begins with "Axiom  v..." — strip the word Axiom we already bold-printed.
        if (leftPart.StartsWith("Axiom", StringComparison.Ordinal))
            return leftPart["Axiom".Length..].EscapeMarkup();
        return leftPart.EscapeMarkup();
    }

    public static string FormatContext(int usedTokens, int contextWindowTokens)
    {
        string used = FormatTokenCount(usedTokens);
        string max = FormatTokenCount(contextWindowTokens);
        return $"{used} / {max}";
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

    public static void WriteWorkedFor(TimeSpan elapsed, int toolCallCount = 0)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string duration = FormatDuration(elapsed);
        string tools = toolCallCount > 0 ? $"  ·  {toolCallCount} tool call{(toolCallCount == 1 ? "" : "s")}" : "";
        AnsiConsole.MarkupLine($"[{muted}]Worked for {duration.EscapeMarkup()}{tools}[/]");
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

    private static string ToolChipsPlain(SessionToolSettings tools)
        => string.Join("  ", tools.AsList().Select(t => (t.Enabled ? "● " : "○ ") + t.Name));

    private static string StripMarkup(string s) => s;

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
        string left = $"{modelLabel}  ·  turn {turnCount}  ·  {StripMarkup(ToolChipsPlain(tools))}";
        string line = BuildLeftRight(left, context, width);
        AnsiConsole.MarkupLine($"[{muted}]{line.EscapeMarkup()}[/]");
    }

    public static void WriteAssistantPrefix()
    {
        AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]axiom[/]  ");
    }

    public static void WriteAgentStatus(string status)
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        try
        {
            int width = Math.Max(20, Console.WindowWidth - 1);
            string text = status.Length > width ? status[..width] : status;
            Console.Write("\r" + text.PadRight(width));
        }
        catch
        {
            AnsiConsole.MarkupLine($"[{muted}]{status.EscapeMarkup()}[/]");
        }
    }

    public static void ClearAgentStatus()
    {
        try
        {
            int width = Math.Max(20, Console.WindowWidth - 1);
            Console.Write("\r" + new string(' ', width) + "\r");
        }
        catch { /* ignore */ }
    }

    private static string BuildLeftRight(string left, string right, int width)
    {
        width = Math.Max(40, width);
        if (left.Length + right.Length + 2 >= width)
            return left + "  " + right;
        int gap = width - 1 - left.Length - right.Length;
        return left + new string(' ', Math.Max(2, gap)) + right;
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

    private static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch { return 100; }
    }

    public sealed class ThinkingIndicator : IDisposable
    {
        private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        private readonly CancellationTokenSource _cts = new();
        private readonly Task? _task;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _gate = new();
        private string _label;
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
                        string text;
                        lock (_gate)
                            text = $"{Frames[frame % Frames.Length]} {_label}... {seconds:0.0}s";
                        Paint(text);
                        frame++;
                        await Task.Delay(80, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public void SetLabel(string label)
        {
            lock (_gate) _label = label;
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
                    Console.Write("\r" + new string(' ', pad) + "\r");
                    _lastLen = 0;
                }
                catch { }
            }
        }

        public void Dispose() => Stop();
    }

    // Writes assistant text with OSC-8 clickable links for http(s) URLs.
    public sealed class TokenStream : IDisposable
    {
        private readonly StringBuilder _buf = new();
        private readonly object _gate = new();
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
                    if (!_started)
                    {
                        WriteAssistantPrefix();
                        _started = true;
                    }
                    LinkText.WriteWithLinks(_buf.ToString());
                    _buf.Clear();
                }

                if (_started)
                    Console.WriteLine();
            }
        }

        public void Dispose() => Complete();
    }
}
