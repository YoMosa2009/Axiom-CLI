using Axiom.Core.Workspace;
using Spectre.Console;

namespace Axiom.Cli.Ui;

internal static class DiffRenderer
{
    public static void Render(string relativePath, string before, string after)
    {
        AnsiConsole.MarkupLine($"[bold {AxiomTheme.Hex(AxiomTheme.TextPrimary)}]{relativePath.EscapeMarkup()}[/]");
        foreach (LineDiffEntry entry in LineDiff.Build(before, after))
        {
            string text = entry.Text.EscapeMarkup();
            switch (entry.Kind)
            {
                case LineDiffKind.Added:
                    AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]+ {text}[/]");
                    break;
                case LineDiffKind.Removed:
                    AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]- {text}[/]");
                    break;
                default:
                    AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]  {text}[/]");
                    break;
            }
        }
    }
}
