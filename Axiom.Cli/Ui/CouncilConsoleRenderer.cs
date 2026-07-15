using System;
using Axiom.Core.Council;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Renders CouncilEvent progress to the terminal — the CLI's equivalent of the WPF app's
// AppendChat/LogActivity sinks, which the orchestrator was deliberately built not to depend on.
internal static class CouncilConsoleRenderer
{
    // Progress<T> marshals callbacks through the captured SynchronizationContext/thread pool,
    // which in a plain console app (no UI thread to protect) reorders output relative to the
    // orchestrator's own await points. A direct synchronous IProgress<T> keeps console lines in
    // the exact order the orchestrator reported them.
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    public static IProgress<CouncilEvent> Create() => new SynchronousProgress<CouncilEvent>(Render);

    private static void Render(CouncilEvent evt)
    {
        switch (evt.Kind)
        {
            case CouncilEventKind.Status:
                AnsiConsole.MarkupLineInterpolated($"[grey]· {evt.Message}[/]");
                break;
            case CouncilEventKind.ArchitectOutput:
                AnsiConsole.Write(new Panel(evt.Message.EscapeMarkup()).Header("[cyan1]Architect[/]").RoundedBorder());
                break;
            case CouncilEventKind.BuilderOutput:
                AnsiConsole.Write(new Panel(Truncate(evt.Message)).Header("[yellow]Builder[/]").RoundedBorder());
                break;
            case CouncilEventKind.CriticOutput:
                AnsiConsole.Write(new Panel(Truncate(evt.Message)).Header("[magenta1]Critic[/]").RoundedBorder());
                break;
            case CouncilEventKind.Warning:
                AnsiConsole.MarkupLineInterpolated($"[yellow]⚠ {evt.Message}[/]");
                break;
            case CouncilEventKind.Completed:
                AnsiConsole.MarkupLineInterpolated($"[green]✓ {evt.Message}[/]");
                break;
            case CouncilEventKind.Failed:
                AnsiConsole.MarkupLineInterpolated($"[red]✗ {evt.Message}[/]");
                break;
        }
    }

    private static string Truncate(string text)
    {
        string escaped = (text ?? string.Empty).EscapeMarkup();
        return escaped.Length > 1200 ? escaped[..1200] + "\n[grey]...truncated...[/]" : escaped;
    }
}
