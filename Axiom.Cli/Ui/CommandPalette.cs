using System;
using System.Collections.Generic;
using System.Linq;

namespace Axiom.Cli.Ui;

// Fuzzy-searchable command entries for Ctrl+K / palette mode.
internal sealed class CommandPaletteItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    /// <summary>Slash command to submit, or special id handled by TUI.</summary>
    public required string Action { get; init; }
}

internal static class CommandPalette
{
    public static IReadOnlyList<CommandPaletteItem> BuildCore()
    {
        return
        [
            new() { Id = "help", Label = "Help", Description = "Show command help", Action = "/help" },
            new() { Id = "mode-auto", Label = "Mode: auto", Description = "Write/shell freely in sandbox", Action = "/mode auto" },
            new() { Id = "mode-ask", Label = "Mode: ask", Description = "Confirm each write/shell", Action = "/mode ask" },
            new() { Id = "mode-plan", Label = "Mode: plan", Description = "No mutations (dry run)", Action = "/mode plan" },
            new() { Id = "mode-cycle", Label = "Cycle mode", Description = "auto → ask → plan", Action = "__cycle_mode__" },
            new() { Id = "undo", Label = "Undo", Description = "Restore last agent file changes", Action = "/undo" },
            new() { Id = "continue", Label = "Continue last task", Description = "Re-run last goal after stop/error", Action = "/continue" },
            new() { Id = "browse", Label = "Browse folder", Description = "Native folder picker", Action = "/browse" },
            new() { Id = "workspace", Label = "Workspace", Description = "Show locked folder", Action = "/workspace" },
            new() { Id = "sessions", Label = "Sessions", Description = "List saved sessions", Action = "/sessions" },
            new() { Id = "session-pick", Label = "Session picker", Description = "↑↓ load · d delete", Action = "__session_picker__" },
            new() { Id = "rename", Label = "Rename session", Description = "Set a title for this chat", Action = "/rename " },
            new() { Id = "export", Label = "Export transcript", Description = "Save markdown to disk", Action = "/export" },
            new() { Id = "export-last", Label = "Export last turn", Description = "Save last user+assistant only", Action = "/export last" },
            new() { Id = "delete", Label = "Delete session", Description = "Delete current + start fresh", Action = "/del" },
            new() { Id = "clear", Label = "Clear transcript", Description = "Clear chat (keeps save file)", Action = "/clear" },
            new() { Id = "council-on", Label = "Council on", Description = "Enable multi-agent", Action = "/tools council on" },
            new() { Id = "council-off", Label = "Council off", Description = "Single agent loop", Action = "/tools council off" },
            new() { Id = "web-on", Label = "Web search on", Description = "Enable live web", Action = "/tools web-search on" },
            new() { Id = "web-off", Label = "Web search off", Description = "Disable live web", Action = "/tools web-search off" },
            new() { Id = "sandbox-on", Label = "Sandbox on", Description = "Python sandbox for Critic", Action = "/tools sandbox on" },
            new() { Id = "sandbox-off", Label = "Sandbox off", Description = "Disable Python sandbox", Action = "/tools sandbox off" },
            new() { Id = "model-eidos", Label = "Model: Eidos 1", Description = "General reasoning", Action = "/model eidos" },
            new() { Id = "model-hepha", Label = "Model: Hepha 1", Description = "Code-specialized", Action = "/model hepha" },
            new() { Id = "resume", Label = "Resume last session", Description = "Load most recent save", Action = "/resume" },
            new() { Id = "exit", Label = "Exit", Description = "Leave Axiom", Action = "/exit" },
        ];
    }

    public static IReadOnlyList<CommandPaletteItem> Filter(IReadOnlyList<CommandPaletteItem> all, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return all;

        string q = query.Trim();
        return all
            .Select(item => (item, score: Score(item, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.item)
            .ToList();
    }

    private static int Score(CommandPaletteItem item, string q)
    {
        string hay = (item.Label + " " + item.Description + " " + item.Id + " " + item.Action)
            .ToLowerInvariant();
        string needle = q.ToLowerInvariant();
        if (hay.Contains(needle, StringComparison.Ordinal))
            return 100 + (hay.StartsWith(needle, StringComparison.Ordinal) ? 20 : 0);
        // Subsequence fuzzy
        int hi = 0;
        foreach (char c in needle)
        {
            int at = hay.IndexOf(c, hi);
            if (at < 0)
                return 0;
            hi = at + 1;
        }
        return 10;
    }
}
