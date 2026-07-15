using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Claude Code / Codex-style line editor. Typing "/" opens a compact dropdown above the prompt;
// ↑↓ moves, Enter selects (toggles tools or runs a command), Esc closes the menu.
internal static class ChatInput
{
    public sealed record SlashItem(
        string Id,
        string Label,
        string Description,
        bool IsTool,
        bool? Enabled);

    public sealed class SlashResult
    {
        public required string Kind { get; init; } // "toggle-tool" | "command"
        public string? ToolName { get; init; }
        public string? Command { get; init; }
    }

    public static string? ReadLine(
        SessionToolSettings tools,
        Func<IReadOnlyList<SlashItem>> buildItems,
        Action<SlashResult> onMenuAction)
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var buffer = new StringBuilder();
        int cursor = 0;
        int selected = 0;
        int blockStartTop = SafeCursorTop();
        int blockHeight = 1; // prompt line only at first

        DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                bool menuOpen = ShouldShowMenu(buffer.ToString());
                if (menuOpen)
                {
                    IReadOnlyList<SlashItem> items = FilteredItems(buildItems(), buffer.ToString());
                    if (items.Count > 0)
                    {
                        SlashItem pick = items[Math.Clamp(selected, 0, items.Count - 1)];
                        string? submit = ApplySelection(pick, onMenuAction, buffer, ref cursor, ref selected);
                        DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                        if (submit != null)
                        {
                            FinishBlock(ref blockStartTop, ref blockHeight);
                            Console.WriteLine();
                            return submit;
                        }
                        continue;
                    }
                }

                FinishBlock(ref blockStartTop, ref blockHeight);
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                if (ShouldShowMenu(buffer.ToString()))
                {
                    // Close the slash palette and clear the partial command.
                    buffer.Clear();
                    cursor = 0;
                    selected = 0;
                    DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                }
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (cursor > 0)
                {
                    buffer.Remove(cursor - 1, 1);
                    cursor--;
                }
                selected = 0;
                DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                if (cursor < buffer.Length)
                    buffer.Remove(cursor, 1);
                selected = 0;
                DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0) cursor--;
                DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buffer.Length) cursor++;
                DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
                DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                cursor = buffer.Length;
                DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                if (!ShouldShowMenu(buffer.ToString()))
                    continue;

                IReadOnlyList<SlashItem> items = FilteredItems(buildItems(), buffer.ToString());
                if (items.Count == 0)
                    continue;

                selected = key.Key == ConsoleKey.UpArrow
                    ? (selected - 1 + items.Count) % items.Count
                    : (selected + 1) % items.Count;
                DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
                continue;

            buffer.Insert(cursor, key.KeyChar);
            cursor++;
            selected = 0;
            DrawBlock(buffer, cursor, selected, buildItems, ref blockStartTop, ref blockHeight);
        }
    }

    // Returns a synthetic submit string for commands that should leave the editor; null to stay.
    private static string? ApplySelection(
        SlashItem pick,
        Action<SlashResult> onMenuAction,
        StringBuilder buffer,
        ref int cursor,
        ref int selected)
    {
        if (pick.IsTool)
        {
            onMenuAction(new SlashResult { Kind = "toggle-tool", ToolName = pick.Id });
            buffer.Clear();
            buffer.Append('/');
            cursor = 1;
            selected = 0;
            return null;
        }

        onMenuAction(new SlashResult { Kind = "command", Command = pick.Id });

        if (pick.Id == "exit")
            return "exit";
        if (pick.Id == "clear")
            return "/clear";
        if (pick.Id == "help")
            return "/help";
        if (pick.Id.StartsWith("model:", StringComparison.Ordinal))
            return "/model " + pick.Id["model:".Length..];

        buffer.Clear();
        cursor = 0;
        selected = 0;
        return null;
    }

    private static void DrawBlock(
        StringBuilder buffer,
        int cursor,
        int selected,
        Func<IReadOnlyList<SlashItem>> buildItems,
        ref int blockStartTop,
        ref int blockHeight)
    {
        string text = buffer.ToString();
        bool showMenu = ShouldShowMenu(text);
        IReadOnlyList<SlashItem> items = showMenu
            ? FilteredItems(buildItems(), text)
            : Array.Empty<SlashItem>();

        int width = SafeWidth();
        var lines = new List<string>();

        if (showMenu)
        {
            lines.Add("  ↑↓ navigate  ·  enter select  ·  esc clears /");
            if (items.Count == 0)
            {
                lines.Add("  (no matches)");
            }
            else
            {
                selected = Math.Clamp(selected, 0, items.Count - 1);
                for (int i = 0; i < items.Count; i++)
                {
                    SlashItem item = items[i];
                    bool active = i == selected;
                    string marker = active ? "❯" : " ";
                    string status = item.IsTool
                        ? (item.Enabled == true ? " ● on " : " ○ off")
                        : "      ";
                    string line = $"  {marker} {item.Label,-12}{status}  {item.Description}";
                    if (line.Length > width - 1)
                        line = line[..Math.Max(8, width - 4)] + "...";
                    lines.Add(line);
                }
            }
        }

        // Prompt line always last.
        lines.Add("❯ " + text);

        // Clear previous block and redraw from blockStartTop.
        try
        {
            int clearRows = Math.Max(blockHeight, lines.Count);
            Console.SetCursorPosition(0, blockStartTop);
            for (int i = 0; i < clearRows; i++)
            {
                Console.Write(new string(' ', width - 1));
                if (i < clearRows - 1)
                {
                    if (blockStartTop + i + 1 >= Console.BufferHeight - 1)
                        Console.WriteLine();
                    else
                        Console.SetCursorPosition(0, blockStartTop + i + 1);
                }
            }

            // If the buffer is short on rows, scroll by writing newlines at end.
            int neededBottom = blockStartTop + lines.Count;
            while (neededBottom >= Console.BufferHeight)
            {
                Console.SetCursorPosition(0, Console.BufferHeight - 1);
                Console.WriteLine();
                blockStartTop = Math.Max(0, blockStartTop - 1);
                neededBottom = blockStartTop + lines.Count;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                Console.SetCursorPosition(0, blockStartTop + i);
                bool isPrompt = i == lines.Count - 1;
                bool isHint = showMenu && i == 0;
                bool isActive = showMenu && items.Count > 0 && i == selected + 1; // +1 for hint row

                if (isPrompt)
                {
                    WriteGold("❯ ");
                    Console.Write(text);
                }
                else if (isActive)
                {
                    WriteGold(lines[i]);
                }
                else if (isHint)
                {
                    WriteMuted(lines[i]);
                }
                else
                {
                    WriteSecondary(lines[i]);
                }
            }

            blockHeight = lines.Count;
            int promptCol = 2 + cursor; // "❯ "
            int promptTop = blockStartTop + lines.Count - 1;
            Console.SetCursorPosition(Math.Min(promptCol, width - 1), promptTop);
        }
        catch
        {
            // Last-resort fallback when the host rejects cursor control.
            Console.Write("\r");
            WriteGold("❯ ");
            Console.Write(text);
            blockHeight = 1;
        }
    }

    private static void FinishBlock(ref int blockStartTop, ref int blockHeight)
    {
        try
        {
            // Park cursor at end of the block so following WriteLine continues below.
            Console.SetCursorPosition(0, blockStartTop + Math.Max(0, blockHeight - 1));
            // Move to end of line content visually.
            Console.Write("\r");
            // Jump past the block.
            int end = Math.Min(Console.BufferHeight - 1, blockStartTop + blockHeight - 1);
            Console.SetCursorPosition(0, end);
        }
        catch { /* ignore */ }
    }

    private static bool ShouldShowMenu(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return false;
        // Hide once the user is typing a normal sentence after a completed command-ish token.
        // Keep open for "/", "/cal", "/model", "/model e".
        if (!text.Contains(' '))
            return true;
        string head = text.Split(' ', 2)[0].ToLowerInvariant();
        return head is "/" or "/tools" or "/model" or "/clear" or "/help"
            || head.StartsWith("/t", StringComparison.Ordinal)
            || head.StartsWith("/m", StringComparison.Ordinal)
            || head.StartsWith("/c", StringComparison.Ordinal)
            || head.StartsWith("/h", StringComparison.Ordinal)
            || head.StartsWith("/e", StringComparison.Ordinal)
            || head.StartsWith("/s", StringComparison.Ordinal)
            || head.StartsWith("/w", StringComparison.Ordinal);
    }

    private static IReadOnlyList<SlashItem> FilteredItems(IReadOnlyList<SlashItem> all, string buffer)
    {
        string filter = buffer.StartsWith('/') ? buffer[1..] : buffer;
        if (filter.StartsWith("model ", StringComparison.OrdinalIgnoreCase))
            filter = filter["model ".Length..];
        else if (filter.StartsWith("tools", StringComparison.OrdinalIgnoreCase))
            filter = filter.Length > 5 ? filter[5..].TrimStart() : string.Empty;

        if (string.IsNullOrWhiteSpace(filter))
            return all;

        return all
            .Where(i => i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || i.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || i.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void WriteGold(string text)
    {
        AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.Gold)}]{text.EscapeMarkup()}[/]");
    }

    private static void WriteMuted(string text)
    {
        AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]{text.EscapeMarkup()}[/]");
    }

    private static void WriteSecondary(string text)
    {
        AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]{text.EscapeMarkup()}[/]");
    }

    private static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch { return 80; }
    }

    private static int SafeCursorTop()
    {
        try { return Console.CursorTop; }
        catch { return 0; }
    }

    public static IReadOnlyList<SlashItem> BuildDefaultItems(
        SessionToolSettings tools,
        IReadOnlyList<(string Id, string Label, string Description)> models)
    {
        var items = new List<SlashItem>
        {
            new("calculator", "calculator", "Math & unit conversion", true, tools.CalculatorEnabled),
            new("web-search", "web-search", "Live web lookup", true, tools.WebSearchEnabled),
            new("sandbox", "sandbox", "Local Python execution", true, tools.SandboxEnabled),
        };

        foreach (var m in models)
            items.Add(new($"model:{m.Id}", m.Label, m.Description, false, null));

        items.Add(new("clear", "clear", "Reset conversation history", false, null));
        items.Add(new("help", "help", "Show command help", false, null));
        items.Add(new("exit", "exit", "Leave chat", false, null));
        return items;
    }
}
