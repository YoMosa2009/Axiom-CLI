using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Optional shell allow/deny policy loaded from .axiom/shell-policy.json under the workspace.
    /// Deny always wins. If allow is non-empty, command must match at least one allow pattern.
    /// </summary>
    public sealed class ShellPolicy
    {
        public IReadOnlyList<string> Allow { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Deny { get; init; } = Array.Empty<string>();

        private static readonly string[] BuiltinDeny =
        [
            @"rm\s+-rf\s+/",
            @"rm\s+-rf\s+~",
            @"del\s+/s\s+/q\s+[a-z]:\\",
            @"format\s+[a-z]:",
            @"mkfs\.",
            @"dd\s+if=",
            @":\(\)\s*\{\s*:\|:&\s*\};:",
            @"shutdown\b",
            @"reboot\b",
            @"git\s+push\s+.*--force",
            @"git\s+push\s+-f\b",
            @"drop\s+database\b",
            @"Invoke-WebRequest.*\|.*iex",
            @"curl\s+.*\|\s*sh\b",
            @"curl\s+.*\|\s*bash\b",
        ];

        public static ShellPolicy Load(string workspaceRoot)
        {
            var policy = new ShellPolicy
            {
                Deny = BuiltinDeny.ToList(),
                Allow = Array.Empty<string>()
            };

            if (string.IsNullOrWhiteSpace(workspaceRoot))
                return policy;

            string path = Path.Combine(workspaceRoot, ".axiom", "shell-policy.json");
            if (!File.Exists(path))
                return policy;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var allow = new List<string>();
                var deny = new List<string>(BuiltinDeny);
                if (root.TryGetProperty("allow", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in a.EnumerateArray())
                    {
                        string? s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            allow.Add(s!);
                    }
                }
                if (root.TryGetProperty("deny", out JsonElement d) && d.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in d.EnumerateArray())
                    {
                        string? s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            deny.Add(s!);
                    }
                }
                return new ShellPolicy { Allow = allow, Deny = deny };
            }
            catch
            {
                return policy;
            }
        }

        public bool TryAuthorize(string command, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                reason = "empty command";
                return false;
            }

            foreach (string pattern in Deny)
            {
                if (Matches(command, pattern))
                {
                    reason = $"denied by shell policy: {pattern}";
                    return false;
                }
            }

            if (Allow.Count > 0)
            {
                bool ok = Allow.Any(p => Matches(command, p));
                if (!ok)
                {
                    reason = "command not on shell allow list (.axiom/shell-policy.json)";
                    return false;
                }
            }

            return true;
        }

        private static bool Matches(string command, string pattern)
        {
            try
            {
                // Treat as regex if it looks like one; else substring (case-insensitive).
                if (pattern.IndexOfAny(new[] { '*', '+', '?', '[', '(', '\\' }) >= 0
                    || pattern.Contains('|'))
                    return Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                return command.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return command.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
