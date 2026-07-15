using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Spectre.Console;

namespace Axiom.Cli.Ui;

internal static class ConsoleUi
{
    private const string Version = "0.1.0";

    public static void ShowWelcome(string modelLabel, SessionToolSettings tools)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Gold)}]{Banner.Ascii.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        var info = new Markup(
            $"[bold {AxiomTheme.Hex(AxiomTheme.TextPrimary)}]Axiom CLI[/] [{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]v{Version} · cloud mode[/]\n" +
            $"\n" +
            $"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]Model[/]  [{AxiomTheme.Hex(AxiomTheme.Gold)}]{modelLabel.EscapeMarkup()}[/]\n" +
            $"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]Tools[/]  {ToolChips(tools)}");

        var tips = new Markup(
            $"[bold {AxiomTheme.Hex(AxiomTheme.TextPrimary)}]Commands[/]\n" +
            $"\n" +
            $"  [{AxiomTheme.Hex(AxiomTheme.Gold)}]/tools[/]   [{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]view or toggle calculator / web-search / sandbox[/]\n" +
            $"  [{AxiomTheme.Hex(AxiomTheme.Gold)}]/model[/]   [{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]switch between Eidos 1 and Hepha 1[/]\n" +
            $"  [{AxiomTheme.Hex(AxiomTheme.Gold)}]/clear[/]   [{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]start a fresh conversation[/]\n" +
            $"  [{AxiomTheme.Hex(AxiomTheme.Gold)}]exit[/]     [{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]quit[/]");

        var panel = new Panel(new Rows(info, new Text(""), tips))
            .Header($"[bold {AxiomTheme.Hex(AxiomTheme.Gold)}]Ready[/]")
            .RoundedBorder()
            .BorderColor(AxiomTheme.Border)
            .Padding(2, 1, 2, 1);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public static void ShowToolsPanel(SessionToolSettings tools)
    {
        var table = new Table()
            .BorderColor(AxiomTheme.Border)
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn($"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]Tool[/]"))
            .AddColumn(new TableColumn($"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]Status[/]"))
            .AddColumn(new TableColumn($"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]Description[/]"));

        foreach ((string name, bool enabled, string description) in ToolDescriptions(tools))
        {
            string status = enabled
                ? $"[{AxiomTheme.Hex(AxiomTheme.Success)}]● on[/]"
                : $"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]○ off[/]";
            table.AddRow(
                $"[{AxiomTheme.Hex(AxiomTheme.TextPrimary)}]{name}[/]",
                status,
                $"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]{description}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Usage: /tools <calculator|web-search|sandbox> <on|off>[/]");
    }

    private static (string Name, bool Enabled, string Description)[] ToolDescriptions(SessionToolSettings tools) =>
    [
        ("calculator", tools.CalculatorEnabled, "Evaluates math expressions and unit conversions in your message"),
        ("web-search", tools.WebSearchEnabled, "Looks up current information when your message needs it"),
        ("sandbox", tools.SandboxEnabled, "Runs Python for numeric/data verification (executes locally — off by default)")
    ];

    public static string ToolChips(SessionToolSettings tools)
    {
        return string.Join("   ", tools.AsList().Select(t =>
            t.Enabled
                ? $"[{AxiomTheme.Hex(AxiomTheme.Success)}]●[/] {t.Name}"
                : $"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]○ {t.Name}[/]"));
    }

    public static void ShowModelPanel(string currentModelLabel, (string Id, string Label, string Description)[] models)
    {
        var table = new Table()
            .BorderColor(AxiomTheme.Border)
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn($"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]Model[/]"))
            .AddColumn(new TableColumn($"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]Description[/]"));

        foreach ((string id, string label, string description) in models)
        {
            bool active = string.Equals(label, currentModelLabel, StringComparison.OrdinalIgnoreCase);
            string name = active
                ? $"[{AxiomTheme.Hex(AxiomTheme.Gold)}]● {label}[/]"
                : $"[{AxiomTheme.Hex(AxiomTheme.TextPrimary)}]○ {label}[/]";
            table.AddRow(name, $"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]{description}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Usage: /model <eidos|hepha>[/]");
    }

    public static void StatusFooter(string modelLabel, SessionToolSettings tools, int turnCount)
    {
        AnsiConsole.MarkupLine(
            $"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]{modelLabel.EscapeMarkup()}[/]  " +
            $"[{AxiomTheme.Hex(AxiomTheme.Border)}]·[/]  " +
            $"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]turn {turnCount}[/]  " +
            $"[{AxiomTheme.Hex(AxiomTheme.Border)}]·[/]  {ToolChips(tools)}");
    }

    public static void PromptDivider()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle(new Style(AxiomTheme.Border)));
    }

    // A lightweight, animated "thinking" indicator shown while waiting for the first token of a
    // streamed response, with a live elapsed-time counter — free-tier OpenRouter models can queue
    // for several seconds, and a bare unmoving prompt reads as "broken" rather than "working".
    // Disposing it clears the line so the response can start printing in its place. No-ops
    // entirely when output is redirected (piped/CI), where cursor control isn't meaningful.
    public sealed class ThinkingIndicator : IDisposable
    {
        private static readonly string[] Frames = ["◐", "◓", "◑", "◒"];
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Threading.Tasks.Task? _task;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _stopped;

        public ThinkingIndicator(string label = "Thinking")
        {
            if (Console.IsOutputRedirected)
                return;

            string gold = AxiomTheme.Hex(AxiomTheme.Gold);
            string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);

            _task = System.Threading.Tasks.Task.Run(async () =>
            {
                int frame = 0;
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        double seconds = _stopwatch.Elapsed.TotalSeconds;
                        AnsiConsole.Markup($"\r[{gold}]{Frames[frame % Frames.Length]}[/] [{muted}]{label}... {seconds:0.0}s[/]   ");
                        frame++;
                        await System.Threading.Tasks.Task.Delay(120, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on Stop().
                }
            });
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            _cts.Cancel();
            try { _task?.Wait(250); } catch { /* best effort */ }

            if (!Console.IsOutputRedirected)
                Console.Write("\r                                                \r");
        }

        public void Dispose() => Stop();
    }
}
