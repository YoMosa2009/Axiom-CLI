using System;
using System.Text;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Raw ANSI helpers for a self-drawn full-window TUI (no Spectre layout / no host scrollback).
internal static class Ansi
{
    public const string Reset = "\u001b[0m";
    public const string ClearScreen = "\u001b[2J";
    public const string ClearLine = "\u001b[2K";
    public const string Home = "\u001b[H";
    public const string EnterAltScreen = "\u001b[?1049h";
    public const string LeaveAltScreen = "\u001b[?1049l";
    public const string HideCursor = "\u001b[?25l";
    public const string ShowCursor = "\u001b[?25h";
    // Enable basic mouse tracking (wheel → as CSI events on supporting hosts).
    public const string EnableMouse = "\u001b[?1000h\u001b[?1006h";
    public const string DisableMouse = "\u001b[?1006l\u001b[?1000l";

    public static string Fg(Color c) => $"\u001b[38;2;{c.R};{c.G};{c.B}m";
    public static string Bg(Color c) => $"\u001b[48;2;{c.R};{c.G};{c.B}m";
    public static string Bold => "\u001b[1m";

    public static string Move(int row1Based, int col1Based) => $"\u001b[{row1Based};{col1Based}H";

    public static string ClipPad(string text, int width)
    {
        text ??= string.Empty;
        // Strip any accidental control chars for width math (we only paint plain + our ANSI).
        if (text.Length > width)
            return text[..Math.Max(0, width - 1)] + "…";
        if (text.Length < width)
            return text + new string(' ', width - text.Length);
        return text;
    }

    public static int VisibleLength(string s)
    {
        if (string.IsNullOrEmpty(s))
            return 0;
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b')
            {
                // skip CSI
                i++;
                while (i < s.Length && s[i] != 'm' && s[i] != 'H' && s[i] != 'K' && s[i] != 'J' && s[i] != 'h' && s[i] != 'l')
                    i++;
                continue;
            }
            n++;
        }
        return n;
    }
}
