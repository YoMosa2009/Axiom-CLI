using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Axiom.Cli.Ui;

// Emits OSC-8 hyperlinks so modern terminals (Windows Terminal, iTerm2, gnome-terminal, etc.)
// make URLs clickable and open them in the default browser.
internal static class LinkText
{
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""')\]]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void WriteWithLinks(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        int last = 0;
        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > last)
                Console.Write(text[last..match.Index]);

            string url = TrimTrailingPunctuation(match.Value);
            WriteHyperlink(url, url);

            // Write any trailing punctuation that was stripped from the match display... we already
            // trimmed from the link target; if original had trailing chars not in url, emit them.
            if (url.Length < match.Value.Length)
                Console.Write(match.Value[url.Length..]);

            last = match.Index + match.Length;
        }

        if (last < text.Length)
            Console.Write(text[last..]);
    }

    public static void WriteHyperlink(string url, string display)
    {
        // OSC 8: ESC ] 8 ; ; URL ST  text ESC ] 8 ; ; ST
        Console.Write($"\u001b]8;;{url}\u001b\\{display}\u001b]8;;\u001b\\");
    }

    private static string TrimTrailingPunctuation(string url)
    {
        while (url.Length > 0 && url[^1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}')
            url = url[..^1];
        return url;
    }
}
