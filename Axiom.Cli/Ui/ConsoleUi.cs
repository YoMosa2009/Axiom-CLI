using System;
using System.Linq;
using System.Threading;
using Spectre.Console;

namespace Axiom.Cli.Ui;

internal static class ConsoleUi
{
    private const string Version = "0.1.0";

    public static void ShowWelcome(string modelLabel, SessionToolSettings tools)
    {
        AnsiConsole.MarkupLine($"[green]{Banner.Ascii.EscapeMarkup()}[/]");

        var tips = new Markup(
            "[bold]Tips[/]\n" +
            "[grey]/tools[/]     toggle calculator / web-search / sandbox\n" +
            "[grey]/clear[/]    start a fresh conversation\n" +
            "[grey]exit[/]      quit");

        var info = new Markup(
            $"[bold]Axiom CLI[/] v{Version}  ·  cloud mode\n" +
            $"Model: [cyan1]{modelLabel.EscapeMarkup()}[/]\n" +
            $"Tools: {ToolChips(tools)}");

        var panel = new Panel(new Rows(info, new Rule().RuleStyle("grey"), tips))
            .Header("[bold]Ready[/]")
            .RoundedBorder()
            .BorderStyle(new Style(Color.Grey));

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public static void ShowToolsPanel(SessionToolSettings tools)
    {
        AnsiConsole.MarkupLine($"Tools: {ToolChips(tools)}");
        AnsiConsole.MarkupLine("[grey]Usage: /tools <calculator|web-search|sandbox> <on|off>[/]");
    }

    public static string ToolChips(SessionToolSettings tools)
    {
        return string.Join("  ", tools.AsList().Select(t =>
            t.Enabled ? $"[green]✓[/] {t.Name}" : $"[grey]✗ {t.Name}[/]"));
    }

    public static void StatusFooter(string modelLabel, SessionToolSettings tools, int turnCount)
    {
        AnsiConsole.MarkupLine(
            $"[grey]{modelLabel.EscapeMarkup()} · turn {turnCount} · {ToolChips(tools)}[/]");
    }

    public static void PromptDivider()
    {
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
    }

    // A lightweight, animated "thinking" indicator shown while waiting for the first token of a
    // streamed response. Disposing it clears the line so the response can start printing in its
    // place — mirrors the "Cooking..." / spinner affordance in comparable coding-agent CLIs.
    // No-ops entirely when output is redirected (piped/CI), where cursor control isn't meaningful.
    public sealed class ThinkingIndicator : IDisposable
    {
        private static readonly string[] Frames = ["◐", "◓", "◑", "◒"];
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Threading.Tasks.Task? _task;
        private int _stopped;

        public ThinkingIndicator(string label = "Thinking")
        {
            if (Console.IsOutputRedirected)
                return;

            _task = System.Threading.Tasks.Task.Run(async () =>
            {
                int frame = 0;
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        Console.Write($"\r[{Frames[frame % Frames.Length]}] {label}...  ");
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
                Console.Write("\r                                  \r");
        }

        public void Dispose() => Stop();
    }
}
