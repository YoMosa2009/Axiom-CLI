using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Council
{
    /// <summary>How aggressively Critic findings block completion / force Builder retries.</summary>
    public enum CriticSeverityPolicy
    {
        /// <summary>Any issue (low+) forces revision when present.</summary>
        Strict = 0,
        /// <summary>Only medium/high/critical block.</summary>
        HighAndAbove = 1,
        /// <summary>Only critical blocks.</summary>
        CriticalOnly = 2
    }

    public enum CouncilDepth
    {
        /// <summary>Full Architect → Builder → Critic with retries.</summary>
        Full = 0,
        /// <summary>Fewer round-trips: combined implement + light review for simple work.</summary>
        Lite = 1
    }

    public enum CouncilRoleVisibility
    {
        /// <summary>Show Architect / Critic cards in the TUI.</summary>
        Full = 0,
        /// <summary>Show final answer + tools; collapse intermediate role prose.</summary>
        FinalOnly = 1
    }

    public static class CriticSeverity
    {
        public static int Rank(string? severity) => (severity ?? "").Trim().ToLowerInvariant() switch
        {
            "critical" => 3,
            "high" => 2,
            "medium" => 1,
            "low" => 0,
            _ => 1
        };

        public static bool Blocks(string? severity, CriticSeverityPolicy policy)
        {
            int r = Rank(severity);
            return policy switch
            {
                CriticSeverityPolicy.CriticalOnly => r >= 3,
                CriticSeverityPolicy.HighAndAbove => r >= 2,
                _ => true
            };
        }

        public static List<CriticIssue> FilterBlocking(IEnumerable<CriticIssue>? issues, CriticSeverityPolicy policy)
        {
            if (issues == null)
                return new List<CriticIssue>();
            return issues.Where(i => Blocks(i.Severity, policy)).ToList();
        }

        public static string Describe(CriticSeverityPolicy p) => p switch
        {
            CriticSeverityPolicy.CriticalOnly => "critical-only",
            CriticSeverityPolicy.HighAndAbove => "high+",
            _ => "strict"
        };

        public static bool TryParse(string raw, out CriticSeverityPolicy policy)
        {
            policy = CriticSeverityPolicy.Strict;
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "strict":
                case "all":
                case "full":
                    policy = CriticSeverityPolicy.Strict;
                    return true;
                case "high":
                case "high+":
                case "medium":
                    policy = CriticSeverityPolicy.HighAndAbove;
                    return true;
                case "critical":
                case "critical-only":
                case "crit":
                    policy = CriticSeverityPolicy.CriticalOnly;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Loads .axiom/acceptance.md (or ACCEPTANCE.md) for Critic always-check criteria.</summary>
    public static class AcceptanceCriteria
    {
        public static string Load(string? workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return string.Empty;

            string[] candidates =
            [
                Path.Combine(workspaceRoot, ".axiom", "acceptance.md"),
                Path.Combine(workspaceRoot, "ACCEPTANCE.md"),
                Path.Combine(workspaceRoot, "acceptance.md")
            ];

            foreach (string path in candidates)
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    string text = File.ReadAllText(path).Trim();
                    if (text.Length == 0)
                        continue;
                    if (text.Length > 6000)
                        text = text[..6000] + "\n...[truncated]";
                    var sb = new StringBuilder();
                    sb.AppendLine("[[ACCEPTANCE CRITERIA — must verify]]");
                    sb.AppendLine($"source: {path}");
                    sb.AppendLine(text);
                    sb.AppendLine("[[END ACCEPTANCE CRITERIA]]");
                    return sb.ToString();
                }
                catch { /* try next */ }
            }

            return string.Empty;
        }
    }
}
