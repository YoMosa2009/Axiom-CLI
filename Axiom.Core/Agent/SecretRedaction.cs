using System;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Strips common secrets from tool results and transcript-bound text.
    /// </summary>
    public static class SecretRedaction
    {
        private static readonly Regex[] Patterns =
        [
            // OpenAI / OpenRouter / generic sk- keys
            new(@"\b(sk-[A-Za-z0-9_\-]{16,})\b", RegexOptions.Compiled),
            new(@"\b(sk-or-[A-Za-z0-9_\-]{16,})\b", RegexOptions.Compiled),
            // GitHub tokens
            new(@"\b(gh[pousr]_[A-Za-z0-9_]{20,})\b", RegexOptions.Compiled),
            new(@"\b(github_pat_[A-Za-z0-9_]{20,})\b", RegexOptions.Compiled),
            // AWS-style
            new(@"\b(AKIA[0-9A-Z]{16})\b", RegexOptions.Compiled),
            // Bearer / Authorization headers
            new(@"(?i)(authorization\s*:\s*bearer\s+)([^\s""']+)", RegexOptions.Compiled),
            new(@"(?i)(x-api-key\s*[:=]\s*)([^\s""']+)", RegexOptions.Compiled),
            // Generic api_key / token assignments in env dumps
            new(@"(?i)\b(api[_-]?key|access[_-]?token|secret[_-]?key|client[_-]?secret)\s*[:=]\s*[""']?([^\s""']{12,})", RegexOptions.Compiled),
            // JWT-looking blobs
            new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled),
        ];

        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            string s = text;
            foreach (var re in Patterns)
            {
                s = re.Replace(s, m =>
                {
                    if (m.Groups.Count >= 3 && m.Groups[2].Success)
                        return m.Groups[1].Value + "[REDACTED]";
                    return "[REDACTED]";
                });
            }
            return s;
        }
    }
}
