using System;
using Axiom.Core.Council;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Renders CouncilEvent progress to the terminal — the CLI's equivalent of the WPF app's
// AppendChat/LogActivity sinks, which the orchestrator was deliberately built not to depend on.
// Role panels use the exact accent colors the GUI's Council chat cards use (WorkplaceChatMessage.cs).
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
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]· {evt.Message.EscapeMarkup()}[/]");
                break;
            case CouncilEventKind.ArchitectOutput:
                WriteRolePanel("Architect", AxiomTheme.Architect, evt.Message);
                break;
            case CouncilEventKind.BuilderOutput:
                WriteRolePanel("Builder", AxiomTheme.Builder, evt.Message);
                break;
            case CouncilEventKind.CriticOutput:
                WriteRolePanel("Critic", AxiomTheme.Critic, evt.Message);
                break;
            case CouncilEventKind.Warning:
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Warning)}]⚠ {evt.Message.EscapeMarkup()}[/]");
                break;
            case CouncilEventKind.Completed:
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]✓ {evt.Message.EscapeMarkup()}[/]");
                break;
            case CouncilEventKind.Failed:
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]✗ {evt.Message.EscapeMarkup()}[/]");
                break;
        }
    }

    private static void WriteRolePanel(string role, Color accent, string message)
    {
        var panel = new Panel(Truncate(message))
            .Header($"[bold {AxiomTheme.Hex(accent)}]{role}[/]")
            .RoundedBorder()
            .BorderColor(accent)
            .Padding(1, 0, 1, 0);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
    }

    private static string Truncate(string text)
    {
        string escaped = (text ?? string.Empty).EscapeMarkup();
        return escaped.Length > 1200
            ? escaped[..1200] + $"\n[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]...truncated...[/]"
            : escaped;
    }
}
