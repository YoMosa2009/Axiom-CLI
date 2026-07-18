using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    // Loads project conventions for the agent/council (Claude Code AGENTS.md / CLAUDE.md pattern).
    public static class ProjectMemory
    {
        public static readonly string[] CandidateNames =
        [
            "AXIOM.md",
            "AGENTS.md",
            "CLAUDE.md",
            Path.Combine(".axiom", "rules.md"),
            Path.Combine(".axiom", "AGENTS.md")
        ];

        public const int MaxTotalChars = 24_000;

        public static string BuildContextBlock(string? workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return string.Empty;

            var chunks = new List<(string Rel, string Body)>();
            int budget = MaxTotalChars;

            foreach (string name in CandidateNames)
            {
                if (budget <= 0)
                    break;

                string full = Path.Combine(workspaceRoot, name);
                if (!File.Exists(full))
                    continue;

                try
                {
                    string body = File.ReadAllText(full);
                    if (string.IsNullOrWhiteSpace(body))
                        continue;
                    if (body.Length > budget)
                        body = body[..budget] + "\n[...project memory truncated...]";
                    string rel = name.Replace('\\', '/');
                    chunks.Add((rel, body));
                    budget -= body.Length;
                }
                catch
                {
                    // skip unreadable
                }
            }

            if (chunks.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[[PROJECT MEMORY — follow these project rules]]");
            sb.AppendLine("Loaded from the connected workspace. Prefer these conventions over generic defaults.");
            foreach ((string rel, string body) in chunks)
            {
                sb.AppendLine($"--- {rel} ---");
                sb.AppendLine(body.TrimEnd());
                sb.AppendLine();
            }
            sb.AppendLine("[[END PROJECT MEMORY]]");
            return sb.ToString();
        }

        public static IReadOnlyList<string> ListLoadedFiles(string? workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return Array.Empty<string>();

            return CandidateNames
                .Select(n => Path.Combine(workspaceRoot, n))
                .Where(File.Exists)
                .Select(p => Path.GetRelativePath(workspaceRoot, p).Replace('\\', '/'))
                .ToList();
        }
    }
}
