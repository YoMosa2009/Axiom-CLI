using Axiom.Core.Workspace;
using Spectre.Console;

namespace Axiom.Cli.Ui;

internal static class DiffRenderer
{
    public static void Render(string relativePath, string before, string after)
    {
        AnsiConsole.MarkupLineInterpolated($"[bold]{relativePath}[/]");
        foreach (LineDiffEntry entry in LineDiff.Build(before, after))
        {
            string text = entry.Text.EscapeMarkup();
            switch (entry.Kind)
            {
                case LineDiffKind.Added:
                    AnsiConsole.MarkupLine($"[green]+ {text}[/]");
                    break;
                case LineDiffKind.Removed:
                    AnsiConsole.MarkupLine($"[red]- {text}[/]");
                    break;
                default:
                    AnsiConsole.MarkupLine($"[grey]  {text}[/]");
                    break;
            }
        }
    }
}
