using System;
using System.Collections.Generic;
using System.Text;

namespace Axiom.Cli.Ui;

// Owns the console as a fixed canvas inside the alternate screen buffer so the host
// scrollbar / scrollback is not part of the UX — we paint every row ourselves.
// Works on Windows Terminal, macOS Terminal/iTerm2, and Linux VTE/xterm-family hosts.
internal sealed class TerminalScreen : IDisposable
{
    private bool _active;
    private bool _mouseEnabled;
    private string[] _last = Array.Empty<string>();

    public int Width { get; private set; } = 80;
    public int Height { get; private set; } = 24;

    public void Enter()
    {
        if (_active)
            return;

        try
        {
            // UTF-8 is required for box drawing + status glyphs on all platforms.
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch { /* some hosts lock encoding */ }

        // Sync console output so paint frames aren't delayed by block buffering (common on Unix).
        try { Console.Out.Flush(); } catch { /* ignore */ }

        Console.Write(Ansi.EnterAltScreen);
        Console.Write(Ansi.HideCursor);

        // Mouse wheel scrolls the transcript. Disable with AXIOM_CLI_MOUSE=0 if paste glitches.
        string? mouseEnv = Environment.GetEnvironmentVariable("AXIOM_CLI_MOUSE");
        bool enableMouse = !string.Equals(mouseEnv, "0", StringComparison.Ordinal);
        if (enableMouse)
        {
            Console.Write(Ansi.EnableMouse);
            // Also enable button-event tracking variants used by Windows Terminal / xterm for wheel.
            Console.Write("\u001b[?1002h\u001b[?1003h");
            _mouseEnabled = true;
        }

        try { Console.CursorVisible = false; } catch { /* ignore */ }

        _active = true;
        RefreshSize();
        ForceClear();
    }

    public void Leave()
    {
        if (!_active)
            return;

        if (_mouseEnabled)
        {
            Console.Write("\u001b[?1003l\u001b[?1002l");
            Console.Write(Ansi.DisableMouse);
        }
        Console.Write(Ansi.ShowCursor);
        Console.Write(Ansi.LeaveAltScreen);
        // Reset SGR so the user's shell doesn't inherit gold/bold colors.
        Console.Write(Ansi.Reset);
        try { Console.CursorVisible = true; } catch { /* ignore */ }
        try { Console.Out.Flush(); } catch { /* ignore */ }
        _active = false;
        _mouseEnabled = false;
    }

    public void Dispose() => Leave();

    public void RefreshSize()
    {
        try
        {
            // On some Unix hosts WindowWidth/Height can be 0 until the first resize — fall back.
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
            if (w <= 1)
                w = TryEnvInt("COLUMNS", 80);
            if (h <= 1)
                h = TryEnvInt("LINES", 24);

            Width = Math.Max(60, w);
            Height = Math.Max(16, h);
        }
        catch
        {
            Width = TryEnvInt("COLUMNS", 100);
            Height = TryEnvInt("LINES", 30);
        }

        if (_last.Length != Height)
            _last = new string[Height];
    }

    private static int TryEnvInt(string name, int fallback)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out int n) && n > 1 ? n : fallback;
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
        {
            Console.Write(sb.ToString());
            try { Console.Out.Flush(); } catch { /* ignore */ }
        }
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
        if (text.IndexOf('\u001b') < 0)
        {
            if (text.Length > width)
                return text[..Math.Max(0, width - 1)] + "…";
            return text.Length < width ? text + new string(' ', width - text.Length) : text;
        }

        int vis = Ansi.VisibleLength(text);
        if (vis > width)
        {
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
