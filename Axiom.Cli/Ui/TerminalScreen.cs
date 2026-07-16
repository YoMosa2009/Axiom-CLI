using System;
using System.Collections.Generic;
using System.Text;

namespace Axiom.Cli.Ui;

// Owns the console as a fixed canvas inside the alternate screen buffer so the host
// scrollbar / scrollback is not part of the UX — we paint every row ourselves.
internal sealed class TerminalScreen : IDisposable
{
    private bool _active;
    private string[] _last = Array.Empty<string>();

    public int Width { get; private set; } = 80;
    public int Height { get; private set; } = 24;

    public void Enter()
    {
        if (_active)
            return;

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { /* ignore */ }

        Console.Write(Ansi.EnterAltScreen);
        Console.Write(Ansi.HideCursor);
        Console.Write(Ansi.EnableMouse);
        try { Console.CursorVisible = false; } catch { /* ignore */ }

        _active = true;
        RefreshSize();
        ForceClear();
    }

    public void Leave()
    {
        if (!_active)
            return;

        Console.Write(Ansi.DisableMouse);
        Console.Write(Ansi.ShowCursor);
        Console.Write(Ansi.LeaveAltScreen);
        try { Console.CursorVisible = true; } catch { /* ignore */ }
        _active = false;
    }

    public void Dispose() => Leave();

    public void RefreshSize()
    {
        try
        {
            Width = Math.Max(60, Console.WindowWidth);
            Height = Math.Max(16, Console.WindowHeight);
        }
        catch
        {
            Width = 100;
            Height = 30;
        }

        if (_last.Length != Height)
            _last = new string[Height];
    }

    public void ForceClear()
    {
        Console.Write(Ansi.ClearScreen + Ansi.Home);
        for (int i = 0; i < _last.Length; i++)
            _last[i] = string.Empty;
    }

    // Paint a full frame of plain (or pre-colored) rows. Each row is clipped/padded to Width.
    // Uses dirty-line updates to reduce flicker.
    public void Paint(IReadOnlyList<string> rows)
    {
        RefreshSize();
        int h = Height;
        int w = Width;

        var sb = new StringBuilder(h * (w + 16));
        for (int r = 0; r < h; r++)
        {
            string raw = r < rows.Count ? rows[r] ?? string.Empty : string.Empty;
            // Ensure row fills width for visual block (ANSI codes allowed; pad based on visible len).
            string line = FitWidth(raw, w);
            if (r < _last.Length && string.Equals(_last[r], line, StringComparison.Ordinal))
                continue;

            sb.Append(Ansi.Move(r + 1, 1));
            sb.Append(Ansi.ClearLine);
            sb.Append(line);
            if (r < _last.Length)
                _last[r] = line;
        }

        if (sb.Length > 0)
            Console.Write(sb.ToString());
    }

    public void ShowCursorAt(int row1Based, int col1Based)
    {
        Console.Write(Ansi.ShowCursor);
        Console.Write(Ansi.Move(Math.Clamp(row1Based, 1, Height), Math.Clamp(col1Based, 1, Width)));
        try { Console.CursorVisible = true; } catch { /* ignore */ }
    }

    public void HideCursor()
    {
        Console.Write(Ansi.HideCursor);
        try { Console.CursorVisible = false; } catch { /* ignore */ }
    }

    private static string FitWidth(string text, int width)
    {
        // If the string has no ANSI, fast path.
        if (text.IndexOf('\u001b') < 0)
        {
            if (text.Length > width)
                return text[..Math.Max(0, width - 1)] + "…";
            return text.Length < width ? text + new string(' ', width - text.Length) : text;
        }

        int vis = Ansi.VisibleLength(text);
        if (vis > width)
        {
            // Truncate carefully is hard with ANSI; fall back to stripping for overflow rows.
            string plain = StripAnsi(text);
            if (plain.Length > width)
                plain = plain[..Math.Max(0, width - 1)] + "…";
            return plain.PadRight(width);
        }

        if (vis < width)
            return text + new string(' ', width - vis);
        return text;
    }

    private static string StripAnsi(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b')
            {
                i++;
                while (i < s.Length && (s[i] < 0x40 || s[i] > 0x7E))
                    i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
