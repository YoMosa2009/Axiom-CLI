using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Axiom.Cli.Ui;

// Lightweight Markdown → ANSI for the chat transcript. Markdown is the right fit for
// model chat (**, `, lists, code fences) — more reliable in a terminal than full LaTeX.
// Optional $math$ / $$math$$ is styled as monospaced italic (no TeX engine).
internal static class MarkdownAnsi
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Unordered = new(@"^(\s*)([-*+])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Ordered = new(@"^(\s*)(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Hr = new(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);

    /// <summary>Render markdown to terminal lines, each colored and wrapped to width (visible cols).</summary>
    public static List<string> RenderLines(string? markdown, int width, Color defaultFg)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(markdown))
            return result;

        width = Math.Max(20, width);
        string text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] rawLines = text.Split('\n');
        int i = 0;

        while (i < rawLines.Length)
        {
            string line = rawLines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                string lang = line.TrimStart()[3..].Trim();
                i++;
                var code = new StringBuilder();
                while (i < rawLines.Length && !rawLines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(rawLines[i]);
                    i++;
                }
                if (i < rawLines.Length)
                    i++;

                string header = string.IsNullOrWhiteSpace(lang) ? "code" : lang.Trim();
                result.Add(Ansi.Fg(AxiomTheme.SystemMuted) + "┌─ " + header + Ansi.Reset);
                foreach (string codeLine in (code.Length == 0 ? new[] { "" } : code.ToString().Split('\n')))
                {
                    foreach (string wrapped in WrapPlain(codeLine, Math.Max(10, width - 2)))
                    {
                        result.Add(
                            Ansi.Fg(AxiomTheme.Border) + "│ " + Ansi.Reset
                            + Ansi.Fg(AxiomTheme.Sandbox) + wrapped + Ansi.Reset);
                    }
                }
                result.Add(Ansi.Fg(AxiomTheme.SystemMuted) + "└" + Ansi.Reset);
                continue;
            }

            if (Hr.IsMatch(line))
            {
                result.Add(Ansi.Fg(AxiomTheme.Border) + new string('─', width) + Ansi.Reset);
                i++;
                continue;
            }

            Match h = Heading.Match(line);
            if (h.Success)
            {
                Color hc = h.Groups[1].Value.Length <= 2 ? AxiomTheme.Gold : AxiomTheme.TextPrimary;
                string colored = RenderInline(h.Groups[2].Value.Trim(), hc, forceBold: true);
                result.AddRange(WrapStyled(colored, width));
                i++;
                continue;
            }

            Match ul = Unordered.Match(line);
            if (ul.Success)
            {
                string indent = ul.Groups[1].Value;
                string body = RenderInline(ul.Groups[3].Value, defaultFg, forceBold: false);
                result.AddRange(WrapWithPrefix(indent + "• ", body, width, AxiomTheme.Gold));
                i++;
                continue;
            }

            Match ol = Ordered.Match(line);
            if (ol.Success)
            {
                string indent = ol.Groups[1].Value;
                string num = ol.Groups[2].Value + ". ";
                string body = RenderInline(ol.Groups[3].Value, defaultFg, forceBold: false);
                result.AddRange(WrapWithPrefix(indent + num, body, width, AxiomTheme.Gold));
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                string q = line.TrimStart();
                while (q.StartsWith('>'))
                    q = q[1..].TrimStart();
                string body = RenderInline(q, AxiomTheme.TextSecondary, forceBold: false);
                result.AddRange(WrapWithPrefix("│ ", body, width, AxiomTheme.Border));
                i++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(string.Empty);
                i++;
                continue;
            }

            result.AddRange(WrapStyled(RenderInline(line, defaultFg, forceBold: false), width));
            i++;
        }

        return result;
    }

    public static string RenderInline(string text, Color defaultFg, bool forceBold)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder();
        void OpenBase()
        {
            sb.Append(Ansi.Fg(defaultFg));
            if (forceBold)
                sb.Append(Ansi.Bold);
        }
        OpenBase();

        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // `code`
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    sb.Append(Ansi.Reset);
                    sb.Append(Ansi.Fg(AxiomTheme.Sandbox));
                    sb.Append(text[(i + 1)..end]);
                    sb.Append(Ansi.Reset);
                    OpenBase();
                    i = end + 1;
                    continue;
                }
            }

            // **bold** / __bold__
            if (TryTakeDelim(text, ref i, "**", out string boldBody)
                || TryTakeDelim(text, ref i, "__", out boldBody))
            {
                sb.Append(Ansi.Bold);
                sb.Append(boldBody);
                sb.Append(Ansi.Reset);
                OpenBase();
                continue;
            }

            // ~~strike~~
            if (TryTakeDelim(text, ref i, "~~", out string strikeBody))
            {
                sb.Append("\u001b[9m");
                sb.Append(strikeBody);
                sb.Append(Ansi.Reset);
                OpenBase();
                continue;
            }

            // *italic* / _italic_
            if ((text[i] == '*' || text[i] == '_')
                && !(i + 1 < text.Length && text[i + 1] == text[i]))
            {
                char d = text[i];
                int end = FindSingleDelim(text, i + 1, d);
                if (end > i)
                {
                    sb.Append("\u001b[3m");
                    sb.Append(text[(i + 1)..end]);
                    sb.Append(Ansi.Reset);
                    OpenBase();
                    i = end + 1;
                    continue;
                }
            }

            // [label](url)
            if (text[i] == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close)
                    {
                        string label = text[(i + 1)..close];
                        string url = text[(close + 2)..urlEnd];
                        sb.Append(Ansi.Fg(AxiomTheme.Architect));
                        sb.Append("\u001b[4m");
                        sb.Append(label);
                        sb.Append(Ansi.Reset);
                        if (!string.IsNullOrWhiteSpace(url)
                            && !string.Equals(label, url, StringComparison.Ordinal))
                        {
                            sb.Append(Ansi.Fg(AxiomTheme.SystemMuted));
                            sb.Append(" (");
                            sb.Append(url);
                            sb.Append(')');
                        }
                        sb.Append(Ansi.Reset);
                        OpenBase();
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            // $$math$$ or $math$
            if (text[i] == '$')
            {
                bool block = i + 1 < text.Length && text[i + 1] == '$';
                int start = block ? i + 2 : i + 1;
                int end = block
                    ? text.IndexOf("$$", start, StringComparison.Ordinal)
                    : text.IndexOf('$', start);
                if (end > start)
                {
                    sb.Append(Ansi.Reset);
                    sb.Append(Ansi.Fg(AxiomTheme.Architect));
                    sb.Append("\u001b[3m");
                    sb.Append(text[start..end]);
                    sb.Append(Ansi.Reset);
                    OpenBase();
                    i = block ? end + 2 : end + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        sb.Append(Ansi.Reset);
        return sb.ToString();
    }

    private static bool TryTakeDelim(string text, ref int i, string delim, out string body)
    {
        body = string.Empty;
        if (i + delim.Length >= text.Length || !text.AsSpan(i).StartsWith(delim))
            return false;
        int search = i + delim.Length;
        int end = text.IndexOf(delim, search, StringComparison.Ordinal);
        if (end <= search)
            return false;
        body = text[search..end];
        i = end + delim.Length;
        return true;
    }

    private static int FindSingleDelim(string text, int from, char d)
    {
        for (int j = from; j < text.Length; j++)
        {
            if (text[j] == '\\' && j + 1 < text.Length)
            {
                j++;
                continue;
            }
            if (text[j] == d)
            {
                if (j + 1 < text.Length && text[j + 1] == d)
                    return -1;
                if (j > from)
                    return j;
            }
        }
        return -1;
    }

    private static List<string> WrapPlain(string text, int width)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            lines.Add(string.Empty);
            return lines;
        }

        int i = 0;
        while (i < text.Length)
        {
            int take = Math.Min(width, text.Length - i);
            if (i + take < text.Length)
            {
                int sp = text.LastIndexOf(' ', i + take - 1, take);
                if (sp >= i + Math.Max(1, width / 4))
                    take = sp - i + 1;
            }
            lines.Add(text.Substring(i, take).TrimEnd());
            i += take;
            while (i < text.Length && text[i] == ' ')
                i++;
        }
        return lines;
    }

    private static List<string> WrapStyled(string ansiText, int width)
    {
        string plain = StripAnsi(ansiText);
        string open = ExtractLeadingSgr(ansiText);
        var lines = new List<string>();
        foreach (string chunk in WrapPlain(plain, width))
            lines.Add(open + chunk + Ansi.Reset);
        return lines;
    }

    private static List<string> WrapWithPrefix(string plainPrefix, string ansiBody, int width, Color prefixColor)
    {
        var lines = new List<string>();
        string plainBody = StripAnsi(ansiBody);
        int firstWidth = Math.Max(8, width - plainPrefix.Length);
        var bodyChunks = WrapPlain(plainBody, firstWidth);
        string openBody = ExtractLeadingSgr(ansiBody);
        lines.Add(Ansi.Fg(prefixColor) + plainPrefix + Ansi.Reset + openBody + bodyChunks[0] + Ansi.Reset);
        string cont = new string(' ', plainPrefix.Length);
        for (int i = 1; i < bodyChunks.Count; i++)
            lines.Add(cont + openBody + bodyChunks[i] + Ansi.Reset);
        return lines;
    }

    private static string ExtractLeadingSgr(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length && s[i] == '\u001b')
        {
            int start = i++;
            if (i < s.Length && s[i] == '[')
            {
                i++;
                while (i < s.Length && s[i] != 'm')
                    i++;
                if (i < s.Length)
                    i++;
                sb.Append(s, start, i - start);
            }
            else break;
        }
        return sb.Length > 0 ? sb.ToString() : Ansi.Fg(AxiomTheme.TextPrimary);
    }

    private static string StripAnsi(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('\u001b') < 0)
            return s ?? string.Empty;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b')
            {
                i++;
                if (i < s.Length && s[i] == '[')
                {
                    while (i < s.Length && s[i] != 'm' && s[i] != 'H' && s[i] != 'K' && s[i] != 'J')
                        i++;
                }
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
