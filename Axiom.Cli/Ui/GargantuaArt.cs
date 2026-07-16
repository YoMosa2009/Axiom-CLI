using System;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Interstellar-inspired Gargantua black-hole ASCII shown once on chat open, then cleared
// on the first user message so the session reclaims the full terminal.
internal static class GargantuaArt
{
    // Pre-measured line count so callers can clear precisely without scrolling chaos.
    public static int LineCount { get; private set; }

    private static readonly string[] Frame =
    [
        @"",
        @"                         .        .        .",
        @"                    .  .:::.   .::::.   .:::.  .",
        @"                 . .::**###**::######::**###**::. .",
        @"               .::**##%%%%%%%%%%##%%%%%%%%%%##**::.",
        @"             .:**##%%%%@@@@@@@@@@@@@@@@@@@%%%%##**:.",
        @"            :*##%%%@@@@@@@###******###@@@@@@@%%%##*:",
        @"           :*##%%@@@@@##**:::......:::**##@@@@@%%##*:",
        @"          :*##%@@@@##*::..            ..::*##@@@@%##*:",
        @"          *##%@@@##*::.     .::::.     .::*##@@@%##*",
        @"         :*#%@@@#*::.    .:*######*:.    .::*#@@@%#*:",
        @"         *##@@@#*:.    .:*##%%%%%%##*:.    .:*#@@@##*",
        @"         *#%@@#*:.    :*##%%@@@@@@%%##*:    .:*#@@%#*",
        @"         *#%@@*:     :*#%@@@@@@@@@@@%#*:     :*@@%#*",
        @"         *#%@@*:    .*#%@@@@(    )@@@@%#*.    :*@@%#*",
        @"         *#%@@*:     :*#%@@@@@@@@@@@%#*:     :*@@%#*",
        @"         *#%@@#*:.    :*##%%@@@@@@%%##*:    .:*#@@%#*",
        @"         *##@@@#*:.    .:*##%%%%%%##*:.    .:*#@@@##*",
        @"         :*#%@@@#*::.    .:*######*:.    .::*#@@@%#*:",
        @"          *##%@@@##*::.     .::::.     .::*##@@@%##*",
        @"          :*##%@@@@##*::..            ..::*##@@@@%##*:",
        @"           :*##%%@@@@@##**:::......:::**##@@@@@%%##*:",
        @"            :*##%%%@@@@@@@###******###@@@@@@@%%%##*:",
        @"             .:**##%%%%@@@@@@@@@@@@@@@@@@@%%%%##**:.",
        @"               .::**##%%%%%%%%%%##%%%%%%%%%%##**::.",
        @"                 . .::**###**::######::**###**::. .",
        @"                    .  .:::.   .::::.   .:::.  .",
        @"                         .        .        .",
        @"",
        @"                      ──  G A R G A N T U A  ──",
        @"                   event horizon · ready to chat",
        @""
    ];

    public static void Render()
    {
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        int width = SafeWidth();

        foreach (string raw in Frame)
        {
            string line = Center(raw, width);
            if (raw.Contains("G A R G A N T U A", StringComparison.Ordinal)
                || raw.Contains("event horizon", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"[{muted}]{line.EscapeMarkup()}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[{gold}]{line.EscapeMarkup()}[/]");
            }
        }

        LineCount = Frame.Length;
    }

    public static void Clear()
    {
        if (LineCount <= 0)
            return;

        try
        {
            // Full clear is the cleanest way to drop a multi-line startup frame.
            Console.Clear();
        }
        catch
        {
            // Hosts that refuse Clear still continue with the session.
        }

        LineCount = 0;
    }

    private static string Center(string text, int width)
    {
        if (string.IsNullOrEmpty(text) || text.Length >= width)
            return text;
        int pad = (width - text.Length) / 2;
        return new string(' ', Math.Max(0, pad)) + text;
    }

    private static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch { return 100; }
    }
}
