using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Full-width input panel docked to the bottom of the visible console window. Layout:
//
//   ╭──────────────────────────────────────────────────────────╮
//   │  prompt text wraps across the box…                       │
//   ╰──────────────────────────────────────────────────────────╯
//    Model  Eidos 1              · · ·              1.2k / 131k
//
// Slash (/) and at (@) menus open above the box.
internal static class ChatInput
{
    public sealed record MenuItem(
        string Id,
        string Label,
        string Description,
        bool IsTool,
        bool? Enabled,
        string Kind);

    public sealed class MenuResult
    {
        public required string Kind { get; init; }
        public string? ToolName { get; init; }
        public string? Command { get; init; }
        public string? FolderPath { get; init; }
    }

    public sealed class InputChrome
    {
        public required string ModelLabel { get; init; }
        public required int UsedTokens { get; init; }
        public required int ContextWindowTokens { get; init; }
        public string? Placeholder { get; init; }
    }

    public static string? ReadLine(
        SessionToolSettings tools,
        InputChrome chrome,
        Func<IReadOnlyList<MenuItem>> buildSlashItems,
        Func<IReadOnlyList<MenuItem>> buildFolderItems,
        Action<MenuResult> onMenuAction)
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var buffer = new StringBuilder();
        int cursor = 0;
        int selected = 0;
        // Reserve a minimum dock height (top+2 content+bottom+model), then pin to viewport bottom.
        int blockHeight = EstimateBlockHeight(buffer.ToString(), menuOpen: false, menuItems: 0);
        int blockStartTop = PinBlockToBottom(blockHeight);

        DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);

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
                        DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
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
                    DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
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
                DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                if (cursor < buffer.Length)
                    buffer.Remove(cursor, 1);
                selected = 0;
                DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0) cursor--;
                selected = 0;
                DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursor < buffer.Length) cursor++;
                selected = 0;
                DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
                DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                cursor = buffer.Length;
                DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
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
                DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
                continue;
            }

            if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
                continue;

            buffer.Insert(cursor, key.KeyChar);
            cursor++;
            selected = 0;
            DrawBlock(buffer, cursor, selected, chrome, buildSlashItems, buildFolderItems, ref blockStartTop, ref blockHeight);
        }
    }

    private enum MenuMode { None, Slash, At }

    private static MenuMode GetMenuMode(string text, int cursor)
    {
        if (string.IsNullOrEmpty(text))
            return MenuMode.None;

        if (text.StartsWith('/') && !text.Contains('\n'))
        {
            if (!text.Contains(' ') || IsKnownSlashHead(text.Split(' ', 2)[0]))
                return MenuMode.Slash;
        }

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

        int at = -1;
        for (int p = i - 1; p >= 0; p--)
        {
            char c = text[p];
            if (c == '@')
            {
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
        InputChrome chrome,
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

        int width = Math.Max(40, ConsoleUi.SafeWidth());
        int inner = Math.Max(20, width - 4); // "│ " + content + " │"
        var lines = new List<string>();

        // Optional dropdown above the box.
        if (mode != MenuMode.None)
        {
            string hint = mode == MenuMode.At
                ? "↑↓ folders  ·  enter attach  ·  esc cancel"
                : "↑↓ navigate  ·  enter select  ·  esc clear";
            lines.Add("  " + hint);

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
            lines.Add(string.Empty);
        }

        // Full-width prompt box.
        string top = "╭" + new string('─', Math.Max(2, width - 3)) + "╮";
        string bottom = "╰" + new string('─', Math.Max(2, width - 3)) + "╯";
        lines.Add(top);

        string display = string.IsNullOrEmpty(text)
            ? (chrome.Placeholder ?? "Message Axiom…  (/ tools · @ folders)")
            : text;

        // Word-wrap content into the box.
        List<string> contentLines = Wrap(display, inner);
        if (contentLines.Count == 0)
            contentLines.Add(string.Empty);

        // Minimum 2 content rows so the box feels like a real field.
        while (contentLines.Count < 2)
            contentLines.Add(string.Empty);

        bool placeholder = string.IsNullOrEmpty(text);
        for (int i = 0; i < contentLines.Count; i++)
        {
            string body = contentLines[i];
            if (body.Length < inner)
                body = body + new string(' ', inner - body.Length);
            else if (body.Length > inner)
                body = body[..inner];
            lines.Add("│ " + body + " │");
        }

        lines.Add(bottom);

        // Seamless model line under the box — model left, context right, wide visual gap.
        string context = ConsoleUi.FormatContext(chrome.UsedTokens, chrome.ContextWindowTokens);
        lines.Add(BuildModelStatusLine(chrome.ModelLabel, context, width));

        try
        {
            // Re-pin when height changes (menu open/close, multi-line wrap).
            int desiredHeight = lines.Count;
            int pinned = PinBlockToBottom(desiredHeight, preferKeep: blockStartTop, previousHeight: blockHeight);
            // Only jump if we can dock lower (more bottom space) or first paint.
            if (pinned > blockStartTop || blockHeight <= 1)
                blockStartTop = pinned;

            int clearRows = Math.Max(blockHeight, lines.Count);
            // Clear previous footprint (may be higher than the new docked position).
            int clearFrom = Math.Min(blockStartTop, Math.Max(0, blockStartTop - Math.Max(0, clearRows - lines.Count)));
            try
            {
                for (int i = 0; i < clearRows + 2; i++)
                {
                    int row = clearFrom + i;
                    if (row < 0 || row >= Console.BufferHeight)
                        continue;
                    Console.SetCursorPosition(0, row);
                    Console.Write(new string(' ', Math.Max(1, width - 1)));
                }
            }
            catch { /* ignore */ }

            int neededBottom = blockStartTop + lines.Count;
            while (neededBottom >= Console.BufferHeight)
            {
                Console.SetCursorPosition(0, Console.BufferHeight - 1);
                Console.WriteLine();
                blockStartTop = Math.Max(0, blockStartTop - 1);
                neededBottom = blockStartTop + lines.Count;
            }

            int menuOffset = 0;
            if (mode != MenuMode.None)
            {
                // hint + items + blank
                menuOffset = 1 + Math.Max(1, items.Count == 0 ? 1 : items.Count) + 1;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                Console.SetCursorPosition(0, blockStartTop + i);
                bool inMenu = mode != MenuMode.None && i < menuOffset;
                bool isActiveMenu = inMenu && items.Count > 0 && i == selected + 1;
                bool isBoxBorder = !inMenu && (lines[i].StartsWith('╭') || lines[i].StartsWith('╰') || lines[i].StartsWith('│'));
                bool isModelLine = i == lines.Count - 1;

                if (isActiveMenu)
                    WriteGold(lines[i]);
                else if (inMenu && i == 0)
                    WriteMuted(lines[i]);
                else if (inMenu)
                    WriteSecondary(lines[i]);
                else if (isModelLine)
                    WriteModelLine(chrome.ModelLabel, context, width);
                else if (isBoxBorder && lines[i].StartsWith('│') && placeholder && i == menuOffset + 1)
                    WritePlaceholderRow(lines[i]);
                else if (isBoxBorder)
                    WriteBorder(lines[i]);
                else
                    WriteSecondary(lines[i]);
            }

            blockHeight = lines.Count;

            // Cursor inside the box content area.
            PlaceCursor(text, cursor, inner, blockStartTop, menuOffset, placeholder);
        }
        catch
        {
            Console.Write("\r");
            WriteGold("❯ ");
            Console.Write(text);
            blockHeight = 1;
        }
    }

    // Docks the input block to the bottom of the *visible* console viewport so the prompt
    // doesn't float mid-window after short transcripts.
    private static int PinBlockToBottom(int blockHeight, int preferKeep = -1, int previousHeight = 0)
    {
        blockHeight = Math.Max(4, blockHeight);
        try
        {
            int windowTop = Console.WindowTop;
            int windowHeight = Math.Max(10, Console.WindowHeight);
            int cursorTop = Console.CursorTop;
            // Leave one blank row above the docked block for breathing room when possible.
            int targetStart = windowTop + windowHeight - blockHeight;
            targetStart = Math.Max(windowTop, targetStart);

            // If chat content already sits below the ideal dock line, keep flowing after content
            // (window will scroll) rather than overwriting transcript.
            if (cursorTop > targetStart && preferKeep < 0)
                return cursorTop;

            // Pad blank lines so the docked region begins at targetStart.
            if (cursorTop < targetStart)
            {
                Console.SetCursorPosition(0, cursorTop);
                for (int i = cursorTop; i < targetStart; i++)
                    Console.WriteLine();
            }

            // When re-drawing an existing docked block, prefer the established top unless the
            // block grew and needs to shift up.
            if (preferKeep >= 0 && previousHeight > 0)
            {
                int grown = blockHeight - previousHeight;
                if (grown > 0)
                    return Math.Max(windowTop, preferKeep - grown);
                return preferKeep;
            }

            return targetStart;
        }
        catch
        {
            return SafeCursorTop();
        }
    }

    private static int EstimateBlockHeight(string text, bool menuOpen, int menuItems)
    {
        int width = Math.Max(40, ConsoleUi.SafeWidth());
        int inner = Math.Max(20, width - 4);
        int content = Math.Max(2, Wrap(string.IsNullOrEmpty(text) ? " " : text, inner).Count);
        int menu = menuOpen ? 1 + Math.Max(1, menuItems) + 1 : 0;
        // top border + content + bottom border + model line
        return menu + 1 + content + 1 + 1;
    }

    private static string BuildModelStatusLine(string modelLabel, string context, int width)
    {
        string left = $"  Model  {modelLabel}";
        string right = context;
        width = Math.Max(40, width - 1);
        int gap = Math.Max(10, width - left.Length - right.Length - 2);
        // Visual separator dots centered in the gap so model and tokens don't sit flush.
        int dots = Math.Clamp(gap / 4, 3, 9);
        int leftPad = Math.Max(3, (gap - dots) / 2);
        int rightPad = Math.Max(3, gap - dots - leftPad);
        return left + new string(' ', leftPad) + new string('·', dots) + new string(' ', rightPad) + right;
    }

    private static void WriteModelLine(string modelLabel, string context, int width)
    {
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string line = BuildModelStatusLine(modelLabel, context, width);
        try
        {
            string prefix = "  Model  ";
            int ctxIdx = line.LastIndexOf(context, StringComparison.Ordinal);
            if (line.StartsWith(prefix, StringComparison.Ordinal) && ctxIdx > prefix.Length)
            {
                AnsiConsole.Markup($"[{muted}]{prefix.EscapeMarkup()}[/]");
                // model name
                string mid = line[prefix.Length..ctxIdx];
                // split name vs dotted spacer
                int nameEnd = 0;
                while (nameEnd < mid.Length && mid[nameEnd] != '·' && !char.IsWhiteSpace(mid[nameEnd]))
                    nameEnd++;
                // actually model name may have spaces — take until we hit a run of spaces before dots
                int dotsAt = mid.IndexOf('·');
                if (dotsAt < 0)
                {
                    AnsiConsole.Markup($"[bold {gold}]{mid.TrimEnd().EscapeMarkup()}[/]");
                }
                else
                {
                    string name = mid[..dotsAt].TrimEnd();
                    string spacer = mid[dotsAt..];
                    AnsiConsole.Markup($"[bold {gold}]{name.EscapeMarkup()}[/]");
                    AnsiConsole.Markup($"[{muted}]{spacer.EscapeMarkup()}[/]");
                }
                AnsiConsole.Markup($"[{muted}]{line[ctxIdx..].EscapeMarkup()}[/]");
            }
            else
            {
                AnsiConsole.Markup($"[{muted}]{line.EscapeMarkup()}[/]");
            }
        }
        catch
        {
            Console.Write(line);
        }
    }

    private static void WritePlaceholderRow(string row)
    {
        // row is like "│ placeholder…     │"
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        string border = AxiomTheme.Hex(AxiomTheme.Border);
        if (row.Length >= 4 && row.StartsWith('│') && row.EndsWith('│'))
        {
            string body = row[2..^2];
            AnsiConsole.Markup($"[{border}]│ [/]");
            AnsiConsole.Markup($"[{muted}]{body.EscapeMarkup()}[/]");
            AnsiConsole.Markup($"[{border}] │[/]");
        }
        else
        {
            WriteBorder(row);
        }
    }

    private static void WriteBorder(string row)
    {
        string border = AxiomTheme.Hex(AxiomTheme.Border);
        string primary = AxiomTheme.Hex(AxiomTheme.TextPrimary);
        if (row.StartsWith('│') && row.EndsWith('│') && row.Length >= 4)
        {
            string body = row[2..^2];
            AnsiConsole.Markup($"[{border}]│ [/]");
            AnsiConsole.Markup($"[{primary}]{body.EscapeMarkup()}[/]");
            AnsiConsole.Markup($"[{border}] │[/]");
        }
        else
        {
            AnsiConsole.Markup($"[{border}]{row.EscapeMarkup()}[/]");
        }
    }

    private static void PlaceCursor(string text, int cursor, int inner, int blockStartTop, int menuOffset, bool placeholder)
    {
        if (placeholder)
        {
            // Start of content area.
            Console.SetCursorPosition(2, blockStartTop + menuOffset + 1);
            return;
        }

        // Map linear cursor into wrapped lines.
        List<string> contentLines = Wrap(text, inner);
        if (contentLines.Count == 0)
            contentLines.Add(string.Empty);

        int remaining = cursor;
        int row = 0;
        int col = 0;
        for (int i = 0; i < contentLines.Count; i++)
        {
            int len = contentLines[i].Length;
            // Wrap() may not include trailing spaces from original; approximate by character walk.
            if (remaining <= len || i == contentLines.Count - 1)
            {
                row = i;
                col = Math.Min(remaining, len);
                break;
            }
            remaining -= len;
            // Account for the break (space consumed).
            // Best-effort: if next char in original was space, wrap consumed it.
        }

        // Safer: recompute col by walking original string with wrap width.
        (row, col) = CursorToRowCol(text, cursor, inner);

        int top = blockStartTop + menuOffset + 1 + row; // +1 past top border
        int left = 2 + col;
        try
        {
            if (top < Console.BufferHeight && left < Console.BufferWidth)
                Console.SetCursorPosition(left, top);
        }
        catch { /* ignore */ }
    }

    private static (int row, int col) CursorToRowCol(string text, int cursor, int inner)
    {
        if (string.IsNullOrEmpty(text) || cursor <= 0)
            return (0, 0);

        int row = 0;
        int col = 0;
        int lineStart = 0;
        for (int i = 0; i < text.Length && i < cursor; i++)
        {
            col++;
            if (col >= inner)
            {
                row++;
                col = 0;
                lineStart = i + 1;
            }
            else if (text[i] == '\n')
            {
                row++;
                col = 0;
                lineStart = i + 1;
            }
        }
        return (row, col);
    }

    private static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(string.Empty);
            return result;
        }

        foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                result.Add(string.Empty);
                continue;
            }

            int i = 0;
            while (i < paragraph.Length)
            {
                int take = Math.Min(width, paragraph.Length - i);
                if (take < paragraph.Length - i)
                {
                    int sp = paragraph.LastIndexOf(' ', i + take - 1, take);
                    if (sp >= i)
                        take = Math.Max(1, sp - i + 1);
                }
                result.Add(paragraph.Substring(i, take).TrimEnd());
                i += take;
            }
        }

        if (result.Count == 0)
            result.Add(string.Empty);
        return result;
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

    private static int SafeCursorTop()
    {
        try { return Console.CursorTop; }
        catch { return 0; }
    }

    public static IReadOnlyList<MenuItem> BuildSlashItems(
        SessionToolSettings tools,
        IReadOnlyList<(string Id, string Label, string Description)> models,
        IReadOnlyList<Axiom.Core.Memory.SessionListItem>? sessions = null)
    {
        var items = new List<MenuItem>
        {
            new("council", "council", "Architect → Builder → Critic multi-agent", true, tools.CouncilEnabled, "slash"),
            new("calculator", "calculator", "Math & unit conversion", true, tools.CalculatorEnabled, "slash"),
            new("web-search", "web-search", "Live web lookup", true, tools.WebSearchEnabled, "slash"),
            new("sandbox", "sandbox", "Local Python execution", true, tools.SandboxEnabled, "slash"),
            new("workspace", "workspace", "Show / lock work folder", false, null, "slash"),
            new("browse", "browse", "Open folder picker (file explorer)", false, null, "slash"),
            new("sessions", "sessions", "List saved chat sessions", false, null, "slash"),
            new("delete", "delete", "Delete current session + start fresh", false, null, "slash"),
            new("undo", "undo", "Undo last agent file changes", false, null, "slash"),
            new("checkpoint", "checkpoint", "Snapshot / restore changed files", false, null, "slash"),
            new("plan", "plan", "Show multi-step plan board", false, null, "slash"),
            new("changes", "changes", "List last-turn file changes", false, null, "slash"),
            new("accept", "accept", "Keep last-turn file changes", false, null, "slash"),
            new("reject", "reject", "Revert selected last-turn files", false, null, "slash"),
            new("replay", "replay", "Re-run last mutating tool plan", false, null, "slash"),
            new("jobs", "jobs", "Background shell jobs", false, null, "slash"),
            new("watch", "watch", "Watch workspace for external edits", false, null, "slash"),
            new("sticky", "sticky", "Sticky multi-turn goal", false, null, "slash"),
            new("pr", "pr", "Push branch + open GitHub PR", false, null, "slash"),
            new("network", "network", "Network: on | off | ask", false, null, "slash"),
            new("policy", "policy", "Shell policy + secret redaction info", false, null, "slash"),
            new("spec", "spec", "Write SPEC.md from this chat", false, null, "slash"),
            new("map", "map", "Show repo map", false, null, "slash"),
            new("mode", "mode", "Set approval: auto | ask | plan", false, null, "slash"),
            new("continue", "continue", "Resume last task after stop/error", false, null, "slash"),
            new("rename", "rename", "Name this session", false, null, "slash"),
            new("export", "export", "Export transcript as markdown", false, null, "slash"),
            new("pick", "pick", "Interactive session picker", false, null, "slash"),
        };

        // One-tap delete rows for recent sessions (seamless — pick from / menu).
        if (sessions is { Count: > 0 })
        {
            int i = 1;
            foreach (var s in sessions.Take(10))
            {
                string title = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title;
                if (title.Length > 36)
                    title = title[..33] + "…";
                items.Add(new(
                    $"session-del:{i}",
                    $"del {i}",
                    $"Delete session: {title}",
                    false,
                    null,
                    "slash"));
                i++;
            }
            items.Add(new("session-del:all", "del all", "Delete ALL saved sessions", false, null, "slash"));
        }

        foreach (var m in models)
            items.Add(new($"model:{m.Id}", m.Label, m.Description, false, null, "slash"));

        items.Add(new("clear", "clear", "Reset conversation history (keeps saved file)", false, null, "slash"));
        items.Add(new("help", "help", "Show command help", false, null, "slash"));
        items.Add(new("exit", "exit", "Leave chat", false, null, "slash"));
        return items;
    }

    public static IReadOnlyList<MenuItem> BuildFolderItems(IReadOnlyList<string> recentFolders)
    {
        var items = new List<MenuItem>
        {
            // Always first: opens the native OS folder browser (Explorer / Finder / file dialog).
            new("__browse__", "Browse…", "Open file explorer and pick a folder", false, null, "folder")
        };

        foreach (string path in recentFolders)
        {
            string name;
            try { name = new System.IO.DirectoryInfo(path).Name; }
            catch { name = path; }
            items.Add(new(path, $"{name}  —  {path}", "Lock workspace here", false, null, "folder"));
        }

        string cwd = Environment.CurrentDirectory;
        if (!items.Any(i => string.Equals(i.Id, cwd, StringComparison.OrdinalIgnoreCase)))
        {
            string name;
            try { name = new System.IO.DirectoryInfo(cwd).Name; }
            catch { name = cwd; }
            items.Add(new(cwd, $"{name}  —  {cwd}  (cwd)", "Lock to current directory", false, null, "folder"));
        }

        return items;
    }
}
