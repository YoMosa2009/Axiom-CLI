using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Axiom.Core.Council
{
    // Lightweight R/C/A contract extracted from the user prompt (desktop GoalContract spirit).
    public sealed class GoalContract
    {
        public List<string> Requirements { get; } = new();
        public List<string> Constraints { get; } = new();
        public List<string> Acceptance { get; } = new();

        public bool HasItems => Requirements.Count + Constraints.Count + Acceptance.Count > 0;

        public static GoalContract FromPrompt(string prompt)
        {
            var c = new GoalContract();
            if (string.IsNullOrWhiteSpace(prompt))
                return c;

            // Explicit bullets / numbered lines become requirements.
            foreach (string raw in prompt.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 4)
                    continue;
                if (Regex.IsMatch(line, @"^(\d+[\.\)]\s+|[-*+]\s+)"))
                {
                    string item = Regex.Replace(line, @"^(\d+[\.\)]\s+|[-*+]\s+)", "").Trim();
                    if (item.Length > 0)
                        c.Requirements.Add(item);
                }
            }

            // Keyword constraints.
            string p = prompt.ToLowerInvariant();
            if (p.Contains("don't") || p.Contains("do not") || p.Contains("without") || p.Contains("must not"))
            {
                foreach (Match m in Regex.Matches(prompt, @"(?i)(?:don't|do not|must not|without)\s+([^.!\n]{8,120})"))
                    c.Constraints.Add(m.Value.Trim());
            }

            // Acceptance defaults for coding tasks.
            if (CouncilRolePrompts.DetectTaskKind(prompt) == CouncilTaskKind.Coding)
            {
                c.Acceptance.Add("Requested behavior is implemented in the connected workspace files.");
                c.Acceptance.Add("No unrelated files or behavior were changed without need.");
                if (p.Contains("test"))
                    c.Acceptance.Add("Tests pass or new tests cover the change.");
            }

            // Cap noise.
            while (c.Requirements.Count > 12) c.Requirements.RemoveAt(c.Requirements.Count - 1);
            while (c.Constraints.Count > 8) c.Constraints.RemoveAt(c.Constraints.Count - 1);
            while (c.Acceptance.Count > 8) c.Acceptance.RemoveAt(c.Acceptance.Count - 1);

            if (c.Requirements.Count == 0 && !string.IsNullOrWhiteSpace(prompt))
            {
                string one = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (one.Length > 200) one = one[..200] + "…";
                c.Requirements.Add(one);
            }

            return c;
        }

        public string ToPromptBlock()
        {
            if (!HasItems)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[[TASK CONTRACT — source of truth]]");
            if (Requirements.Count > 0)
            {
                sb.AppendLine("Requirements (R):");
                for (int i = 0; i < Requirements.Count; i++)
                    sb.AppendLine($"  R{i + 1}. {Requirements[i]}");
            }
            if (Constraints.Count > 0)
            {
                sb.AppendLine("Constraints (C):");
                for (int i = 0; i < Constraints.Count; i++)
                    sb.AppendLine($"  C{i + 1}. {Constraints[i]}");
            }
            if (Acceptance.Count > 0)
            {
                sb.AppendLine("Acceptance (A):");
                for (int i = 0; i < Acceptance.Count; i++)
                    sb.AppendLine($"  A{i + 1}. {Acceptance[i]}");
            }
            sb.AppendLine("Architect maps R/C/A to steps. Builder implements. Critic verifies each item against evidence.");
            sb.AppendLine("[[END TASK CONTRACT]]");
            return sb.ToString();
        }
    }
}
