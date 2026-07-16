using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Line editor with two palettes:
//   /  → tools & commands
//   @  → recent / attachable folders
internal static class ChatInput
{
    public sealed record MenuItem(
        string Id,
        string Label,
        string Description,
        bool IsTool,
        bool? Enabled,
        string Kind); // "slash" | "folder"

    public sealed class MenuResult
    {
        public required string Kind { get; init; } // "toggle-tool" | "command" | "attach-folder"
        public string? ToolName { get; init; }
        public string? Command { get; init; }
        public string? FolderPath { get; init; }
    }

    public static string? ReadLine(
        SessionToolSettings tools,
        Func<IReadOnlyList<MenuItem>> buildSlashItems,
        Func<IReadOnlyList<MenuItem>> buildFolderItems,
        Action<MenuResult> onMenuAction)
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var buffer = new StringBuilder();
        int cursor = 0;
        int selected = 0;
        int blockStartTop = SafeCursorTop();
        int blockHeight = 1;

        DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                string text = buffer.ToString();
                MenuMode mode = GetMenuMode(text, cursor);
                if (mode != MenuMode.None)
                {
                    IReadOnlyList<MenuItem> items = GetFilteredItems(text, cursor, mode, buildSlashItems, buildFolderItems);
                    if (items.Count > 0)
                    {
                        MenuItem pick = items[Math.Clamp(selected, 0, items.Count - 1)];
                        string? submit = ApplySelection(pick, onMenuAction, buffer, ref cursor, ref selected, mode);
                        DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
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
                if (GetMenuMode(buffer.ToString(), cursor) != MenuMode.None)
                {
                    // Close active token only.
                    if (GetMenuMode(buffer.ToString(), cursor) == MenuMode.Slash && buffer.ToString().TrimStart().StartsWith('/'))
                    {
                        buffer.Clear();
                        cursor = 0;
                    }
                    else
                    {
                        RemoveActiveAtToken(buffer, ref cursor);
                    }
                    selected = 0;
                    DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
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
                DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                if (cursor < buffer.Length)
                    buffer.Remove(cursor, 1);
                selected = 0;
                DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0) cursor--;
                selected = 0;
                DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buffer.Length) cursor++;
                selected = 0;
                DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
                DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                cursor = buffer.Length;
                DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                MenuMode mode = GetMenuMode(buffer.ToString(), cursor);
                if (mode == MenuMode.None)
                    continue;

                IReadOnlyList<MenuItem> items = GetFilteredItems(buffer.ToString(), cursor, mode, buildSlashItems, buildFolderItems);
                if (items.Count == 0)
                    continue;

                selected = key.Key == ConsoleKey.UpArrow
                    ? (selected - 1 + items.Count) % items.Count
                    : (selected + 1) % items.Count;
                DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
                continue;

            buffer.Insert(cursor, key.KeyChar);
            cursor++;
            selected = 0;
            DrawBlock(buffer, cursor, selected, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
        }
    }

    private enum MenuMode { None, Slash, At }

    private static MenuMode GetMenuMode(string text, int cursor)
    {
        if (string.IsNullOrEmpty(text))
            return MenuMode.None;

        // Slash menu: buffer is a slash command (starts with / and no newline)
        if (text.StartsWith('/') && !text.Contains('\n'))
        {
            // Still slash-mode while typing command tokens.
            if (!text.Contains(' ') || IsKnownSlashHead(text.Split(' ', 2)[0]))
                return MenuMode.Slash;
        }

        // @ menu: active when cursor sits in an @token
        if (TryGetAtToken(text, cursor, out _, out _, out _))
            return MenuMode.At;

        return MenuMode.None;
    }

    private static bool IsKnownSlashHead(string head)
    {
        head = head.ToLowerInvariant();
        return head is "/" or "/tools" or "/model" or "/clear" or "/help" or "/workspace"
            || head.StartsWith("/t") || head.StartsWith("/m") || head.StartsWith("/c")
            || head.StartsWith("/h") || head.StartsWith("/e") || head.StartsWith("/s")
            || head.StartsWith("/w") || head.StartsWith("/a");
    }

    private static bool TryGetAtToken(string text, int cursor, out int start, out int end, out string token)
    {
        start = end = 0;
        token = string.Empty;
        int i = Math.Clamp(cursor, 0, text.Length);

        // Find '@' before cursor with no whitespace between.
        int at = -1;
        for (int p = i - 1; p >= 0; p--)
        {
            char c = text[p];
            if (c == '@')
            {
                // Start of token if at beginning or preceded by whitespace
                if (p == 0 || char.IsWhiteSpace(text[p - 1]))
                {
                    at = p;
                    break;
                }
                return false;
            }
            if (char.IsWhiteSpace(c))
                return false;
        }

        if (at < 0)
            return false;

        end = at + 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        // Cursor must be inside the token
        if (cursor < at || cursor > end)
            return false;

        start = at;
        token = text[at..end];
        return true;
    }

    private static void RemoveActiveAtToken(StringBuilder buffer, ref int cursor)
    {
        string text = buffer.ToString();
        if (!TryGetAtToken(text, cursor, out int start, out int end, out _))
            return;
        buffer.Remove(start, end - start);
        cursor = start;
    }

    private static IReadOnlyList<MenuItem> GetFilteredItems(
        string text,
        int cursor,
        MenuMode mode,
        Func<IReadOnlyList<MenuItem>> buildSlashItems,
        Func<IReadOnlyList<MenuItem>> buildFolderItems)
    {
        if (mode == MenuMode.Slash)
        {
            string filter = text.StartsWith('/') ? text[1..] : text;
            if (filter.StartsWith("model ", StringComparison.OrdinalIgnoreCase))
                filter = filter["model ".Length..];
            else if (filter.StartsWith("tools", StringComparison.OrdinalIgnoreCase))
                filter = filter.Length > 5 ? filter[5..].TrimStart() : string.Empty;

            return Filter(buildSlashItems(), filter);
        }

        if (mode == MenuMode.At && TryGetAtToken(text, cursor, out _, out _, out string token))
        {
            string filter = token.StartsWith('@') ? token[1..] : token;
            return Filter(buildFolderItems(), filter);
        }

        return Array.Empty<MenuItem>();
    }

    private static IReadOnlyList<MenuItem> Filter(IReadOnlyList<MenuItem> all, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return all;
        return all
            .Where(i => i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || i.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || i.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string? ApplySelection(
        MenuItem pick,
        Action<MenuResult> onMenuAction,
        StringBuilder buffer,
        ref int cursor,
        ref int selected,
        MenuMode mode)
    {
        if (mode == MenuMode.At || pick.Kind == "folder")
        {
            onMenuAction(new MenuResult { Kind = "attach-folder", FolderPath = pick.Id });
            // Replace @token with the path (quoted if spaces)
            string text = buffer.ToString();
            if (TryGetAtToken(text, cursor, out int start, out int end, out _))
            {
                string insert = pick.Id.Contains(' ') ? $"\"{pick.Id}\"" : pick.Id;
                buffer.Remove(start, end - start);
                buffer.Insert(start, insert + " ");
                cursor = start + insert.Length + 1;
            }
            selected = 0;
            return null;
        }

        if (pick.IsTool)
        {
            onMenuAction(new MenuResult { Kind = "toggle-tool", ToolName = pick.Id });
            buffer.Clear();
            buffer.Append('/');
            cursor = 1;
            selected = 0;
            return null;
        }

        onMenuAction(new MenuResult { Kind = "command", Command = pick.Id });

        if (pick.Id == "exit") return "exit";
        if (pick.Id == "clear") return "/clear";
        if (pick.Id == "help") return "/help";
        if (pick.Id == "workspace") return "/workspace";
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
        Func<IReadOnlyList<MenuItem>> buildSlashItems,
        Func<IReadOnlyList<MenuItem>> buildFolderItems,
        ref int blockStartTop,
        ref int blockHeight)
    {
        string text = buffer.ToString();
        MenuMode mode = GetMenuMode(text, cursor);
        IReadOnlyList<MenuItem> items = mode == MenuMode.None
            ? Array.Empty<MenuItem>()
            : GetFilteredItems(text, cursor, mode, buildSlashItems, buildFolderItems);

        int width = SafeWidth();
        var lines = new List<string>();

        if (mode != MenuMode.None)
        {
            string hint = mode == MenuMode.At
                ? "  ↑↓ folders  ·  enter attach  ·  esc cancel"
                : "  ↑↓ navigate  ·  enter select  ·  esc clear";
            lines.Add(hint);

            if (items.Count == 0)
            {
                lines.Add(mode == MenuMode.At ? "  (no recent folders — type a path)" : "  (no matches)");
            }
            else
            {
                selected = Math.Clamp(selected, 0, items.Count - 1);
                for (int i = 0; i < items.Count; i++)
                {
                    MenuItem item = items[i];
                    bool active = i == selected;
                    string marker = active ? "❯" : " ";
                    string status = item.IsTool
                        ? (item.Enabled == true ? " ● on " : " ○ off")
                        : "      ";
                    string line = mode == MenuMode.At
                        ? $"  {marker} {item.Label}"
                        : $"  {marker} {item.Label,-12}{status}  {item.Description}";
                    if (line.Length > width - 1)
                        line = line[..Math.Max(8, width - 4)] + "...";
                    lines.Add(line);
                }
            }
        }

        lines.Add("❯ " + text);

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
                bool isHint = mode != MenuMode.None && i == 0;
                bool isActive = mode != MenuMode.None && items.Count > 0 && i == selected + 1;

                if (isPrompt)
                {
                    WriteGold("❯ ");
                    Console.Write(text);
                }
                else if (isActive)
                    WriteGold(lines[i]);
                else if (isHint)
                    WriteMuted(lines[i]);
                else
                    WriteSecondary(lines[i]);
            }

            blockHeight = lines.Count;
            int promptCol = 2 + cursor;
            int promptTop = blockStartTop + lines.Count - 1;
            Console.SetCursorPosition(Math.Min(promptCol, width - 1), promptTop);
        }
        catch
        {
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
            int end = Math.Min(Console.BufferHeight - 1, blockStartTop + blockHeight - 1);
            Console.SetCursorPosition(0, end);
        }
        catch { }
    }

    private static void WriteGold(string text)
        => AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.Gold)}]{text.EscapeMarkup()}[/]");

    private static void WriteMuted(string text)
        => AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]{text.EscapeMarkup()}[/]");

    private static void WriteSecondary(string text)
        => AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.TextSecondary)}]{text.EscapeMarkup()}[/]");

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

    public static IReadOnlyList<MenuItem> BuildSlashItems(
        SessionToolSettings tools,
        IReadOnlyList<(string Id, string Label, string Description)> models)
    {
        var items = new List<MenuItem>
        {
            new("calculator", "calculator", "Math & unit conversion", true, tools.CalculatorEnabled, "slash"),
            new("web-search", "web-search", "Live web lookup", true, tools.WebSearchEnabled, "slash"),
            new("sandbox", "sandbox", "Local Python execution", true, tools.SandboxEnabled, "slash"),
            new("workspace", "workspace", "Show attached folders", false, null, "slash"),
        };

        foreach (var m in models)
            items.Add(new($"model:{m.Id}", m.Label, m.Description, false, null, "slash"));

        items.Add(new("clear", "clear", "Reset conversation history", false, null, "slash"));
        items.Add(new("help", "help", "Show command help", false, null, "slash"));
        items.Add(new("exit", "exit", "Leave chat", false, null, "slash"));
        return items;
    }

    public static IReadOnlyList<MenuItem> BuildFolderItems(IReadOnlyList<string> recentFolders)
    {
        var items = new List<MenuItem>();
        foreach (string path in recentFolders)
        {
            string label;
            try { label = path; }
            catch { label = path; }

            string desc;
            try { desc = new System.IO.DirectoryInfo(path).Name; }
            catch { desc = path; }

            // Show folder name as label, full path as description if space.
            string name;
            try { name = new System.IO.DirectoryInfo(path).Name; }
            catch { name = path; }

            items.Add(new(path, $"{name}  —  {path}", "Attach workspace", false, null, "folder"));
        }

        // Always offer the current directory.
        string cwd = Environment.CurrentDirectory;
        if (!items.Any(i => string.Equals(i.Id, cwd, StringComparison.OrdinalIgnoreCase)))
        {
            string name;
            try { name = new System.IO.DirectoryInfo(cwd).Name; }
            catch { name = cwd; }
            items.Insert(0, new(cwd, $"{name}  —  {cwd}  (cwd)", "Current directory", false, null, "folder"));
        }

        return items;
    }
}
