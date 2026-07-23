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
        public List<string> ExactLiterals { get; } = new();
        public List<string> RequiredArtifactExtensions { get; } = new();
        public List<string> Acceptance { get; } = new();
        public bool RequiresLiteralPresenceInWrittenArtifacts { get; private set; }
        public bool RequiresWrittenArtifacts { get; private set; }
        public bool RequiresInteractiveBehavior { get; private set; }

        public bool HasItems => Requirements.Count + Constraints.Count + ExactLiterals.Count + Acceptance.Count > 0;

        public static GoalContract FromPrompt(string prompt)
        {
            var c = new GoalContract();
            if (string.IsNullOrWhiteSpace(prompt))
                return c;

            string sourcePrompt = StripInjectedToolBlocks(prompt);
            string normalized = Regex.Replace(sourcePrompt.Replace('\r', '\n'), @"[ \t]+", " ").Trim();

            // Preserve exact user-facing copy, identifiers, and other quoted literals. These are
            // cheap, deterministic acceptance checks that a small model cannot paraphrase away.
            foreach (Match match in Regex.Matches(normalized, "[\"“](?<literal>[^\"”\\n]{2,160})[\"”]"))
            {
                string literal = match.Groups["literal"].Value.Trim();
                int prefixStart = Math.Max(0, match.Index - 40);
                string prefix = normalized[prefixStart..match.Index];
                if (Regex.IsMatch(prefix, @"(?i)(?:remove|delete|omit|exclude|without|do not include|must not include|replace)\s*$"))
                    continue;
                // In prose such as `for “Product Name.”` the period is sentence punctuation,
                // not part of the named entity. Preserve punctuation for normal quoted copy.
                if (Regex.IsMatch(prefix, @"(?i)\b(?:for|named|called|titled)\s*$"))
                    literal = literal.TrimEnd('.', '!', '?');
                AddUnique(c.ExactLiterals, literal, 12);
            }

            // Natural prompts are usually prose, not bullet lists. Split sentences/newlines and
            // semicolon-delimited clauses so later requirements do not disappear behind the old
            // single 200-character fallback.
            foreach (string raw in Regex.Split(
                normalized,
                @"(?:(?<=[.!?])|(?<=[.!?][""”]))\s+|[\n;]+"))
            {
                string item = Regex.Replace(raw.Trim(), @"^(\d+[\.\)]\s+|[-*+]\s+)", "").Trim();
                if (item.Length < 4)
                    continue;
                if (item.Length > 280)
                    item = item[..280].TrimEnd() + "…";

                bool negative = Regex.IsMatch(item, @"(?i)\b(don't|do not|must not|without|avoid|never)\b");
                AddUnique(negative ? c.Constraints : c.Requirements, item, negative ? 8 : 14);
            }

            string p = sourcePrompt.ToLowerInvariant();
            c.RequiresLiteralPresenceInWrittenArtifacts = Regex.IsMatch(
                p,
                @"\b(create|build|make|generate|scaffold|add|insert|write|design|develop|produce)\b");
            c.RequiresWrittenArtifacts = CouncilOrchestrator.LooksLikeCodeEditRequest(sourcePrompt);

            // When the user names a file type, do not accept a directory, a note, or a different
            // file type as a completed deliverable. Ignore explicitly rejected types, such as
            // "do not make an .exe; make an .html".
            foreach (Match match in Regex.Matches(
                sourcePrompt,
                @"(?<![\w.])\.(?<extension>html?|css|js|jsx|ts|tsx|cs|py|java|go|rs|cpp|c|h|json|xml|ya?ml|md|txt|sql|sh|ps1|bat|exe)\b",
                RegexOptions.IgnoreCase))
            {
                int prefixStart = Math.Max(0, match.Index - 56);
                string prefix = sourcePrompt[prefixStart..match.Index];
                if (Regex.IsMatch(prefix, @"(?i)\b(?:don't|do not|must not|without|avoid|never|instead of|not)\b[^\r\n.;:]{0,40}$"))
                    continue;

                AddUnique(c.RequiredArtifactExtensions, "." + match.Groups["extension"].Value.ToLowerInvariant(), 8);
            }

            c.RequiresInteractiveBehavior = c.RequiresWrittenArtifacts
                && Regex.IsMatch(
                    p,
                    @"\b(playable|interactive|interact|clickable|click|drag(?:gable)?|keyboard|game|animation|animated|form\s+(?:submit|validation)|user\s+input)\b");
            bool implementationTask =
                CouncilRolePrompts.DetectTaskKind(sourcePrompt) == CouncilTaskKind.Coding
                || c.RequiresWrittenArtifacts;
            if (implementationTask)
            {
                c.Acceptance.Add("The requested artifact or behavior is implemented in the connected workspace files.");
                c.Acceptance.Add("Every R/C/L item is checked against the written artifacts before completion.");
                c.Acceptance.Add("Written artifacts pass validation appropriate to their file and interface type.");
                c.Acceptance.Add("No unrelated files or behavior are changed without need.");
                if (p.Contains("test"))
                    c.Acceptance.Add("Tests pass or new tests cover the change.");
            }
            if (c.RequiresWrittenArtifacts && c.RequiredArtifactExtensions.Count > 0)
                c.Acceptance.Add("At least one written artifact has each explicitly requested file type.");
            if (c.RequiresInteractiveBehavior)
                c.Acceptance.Add("Requested interactive behavior is implemented by executable artifact code, not described as future work.");

            if (c.Requirements.Count == 0 && !string.IsNullOrWhiteSpace(prompt))
            {
                string one = normalized;
                if (one.Length > 280) one = one[..280] + "…";
                c.Requirements.Add(one);
            }

            return c;
        }

        private static string StripInjectedToolBlocks(string prompt)
        {
            int cut = -1;
            string[] markers = ["[[web search", "[[python sandbox", "[[calculator"];
            foreach (string marker in markers)
            {
                int candidate = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (candidate >= 0 && (cut < 0 || candidate < cut))
                    cut = candidate;
            }
            return cut > 0 ? prompt[..cut].TrimEnd() : prompt;
        }

        private static void AddUnique(List<string> destination, string value, int limit)
        {
            if (destination.Count >= limit || string.IsNullOrWhiteSpace(value))
                return;
            if (!destination.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                destination.Add(value);
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
            if (ExactLiterals.Count > 0)
            {
                sb.AppendLine("Exact literals (L) — preserve verbatim:");
                for (int i = 0; i < ExactLiterals.Count; i++)
                    sb.AppendLine($"  L{i + 1}. {ExactLiterals[i]}");
            }
            if (RequiredArtifactExtensions.Count > 0)
                sb.AppendLine("Required artifact types: " + string.Join(", ", RequiredArtifactExtensions));
            if (Acceptance.Count > 0)
            {
                sb.AppendLine("Acceptance (A):");
                for (int i = 0; i < Acceptance.Count; i++)
                    sb.AppendLine($"  A{i + 1}. {Acceptance[i]}");
            }
            sb.AppendLine("Architect maps every R/C/L/A item to steps. Builder implements each item. Critic verifies each item against actual artifact evidence.");
            sb.AppendLine("[[END TASK CONTRACT]]");
            return sb.ToString();
        }
    }
}
