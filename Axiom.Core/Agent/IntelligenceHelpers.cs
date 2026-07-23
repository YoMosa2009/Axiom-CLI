using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Axiom.Core.Chat;
using Axiom.Core.Council;

namespace Axiom.Core.Agent
{
    public enum TaskSpecialty
    {
        General,
        Debug,
        Greenfield,
        Review,
        Refactor,
        Docs
    }

    public static class IntelligenceHelpers
    {
        public static TaskSpecialty DetectSpecialty(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return TaskSpecialty.General;
            string p = prompt.ToLowerInvariant();
            if (Regex.IsMatch(p, @"\b(bug|fix|error|exception|crash|stack\s*trace|failing|broken|regress)\b"))
                return TaskSpecialty.Debug;
            if (Regex.IsMatch(p, @"\b(review|audit|critique|look over|code review)\b"))
                return TaskSpecialty.Review;
            if (Regex.IsMatch(p, @"\b(refactor|clean up|restructure|rename|extract)\b"))
                return TaskSpecialty.Refactor;
            if (Regex.IsMatch(p, @"\b(scaffold|from scratch|new project|greenfield|create app|bootstrap)\b")
                || Regex.IsMatch(p, @"\b(create|build|make|design|develop)\b.{0,40}\b(website|webpage|landing\s+page|web\s+app|user\s+interface|ui\s+component)\b"))
                return TaskSpecialty.Greenfield;
            if (Regex.IsMatch(p, @"\b(document|docs|readme|comment|explain only)\b"))
                return TaskSpecialty.Docs;
            return TaskSpecialty.General;
        }

        public static string SpecialtyPromptBlock(TaskSpecialty s) => s switch
        {
            TaskSpecialty.Debug =>
                "[[TASK SPECIALTY: DEBUG]]\n" +
                "Reproduce or locate the failure first (search_files / read_file / run_tests). " +
                "Change the minimal surface. After fix, re-run the failing test if known.\n" +
                "[[END TASK SPECIALTY]]",
            TaskSpecialty.Greenfield =>
                "[[TASK SPECIALTY: GREENFIELD]]\n" +
                "Create a runnable vertical slice, then fully implement every task-contract item. " +
                "A skeleton or scaffold is an intermediate state, never the final deliverable. Prefer clear file layout.\n" +
                "[[END TASK SPECIALTY]]",
            TaskSpecialty.Review =>
                "[[TASK SPECIALTY: REVIEW]]\n" +
                "Do not rewrite unless asked. Cite file:line findings. Separate blockers vs nits.\n" +
                "[[END TASK SPECIALTY]]",
            TaskSpecialty.Refactor =>
                "[[TASK SPECIALTY: REFACTOR]]\n" +
                "Preserve behavior. Prefer str_replace. Run tests after structural moves.\n" +
                "[[END TASK SPECIALTY]]",
            TaskSpecialty.Docs =>
                "[[TASK SPECIALTY: DOCS]]\n" +
                "Prefer editing markdown/comments. Avoid drive-by code changes.\n" +
                "[[END TASK SPECIALTY]]",
            _ => string.Empty
        };

        public static string UncertaintyInstruction =>
            "[[UNCERTAINTY]] If requirements conflict or a critical fact is missing, ask at most ONE clarifying question " +
            "before large edits. Otherwise state a brief assumption and proceed. [[END UNCERTAINTY]]";

        public static string ArchitectValidationError(string plan, CouncilTaskKind kind, bool codingPathsRequired)
        {
            if (string.IsNullOrWhiteSpace(plan) || plan.Trim().Length < 4)
                return "Plan is empty or too short.";

            int numbered = Regex.Matches(plan, @"(?m)^\s*\d+[\.\)]\s+\S+").Count;
            if (numbered < 1)
                return "Plan must contain at least one numbered step.";

            if (codingPathsRequired || kind == CouncilTaskKind.Coding)
            {
                bool hasPath = Regex.IsMatch(plan, @"[\w./\\-]+\.(cs|ts|tsx|js|jsx|py|go|rs|java|json|md|html|css)\b",
                    RegexOptions.IgnoreCase)
                    || Regex.IsMatch(plan, @"\b(src|lib|app|tests?)/[\w./\\-]+", RegexOptions.IgnoreCase);
                // Only hard-fail long coding plans that never name a file/path
                if (!hasPath && plan.Length > 120
                    && Regex.IsMatch(plan, @"\b(file|class|function|module|component|endpoint|implement)\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(plan, @"\b[\w-]+\.\w{1,5}\b"))
                    return "Coding plan should name at least one concrete file path.";
            }

            // Vague verbs without substance (only when multi-step and very vague)
            int vague = Regex.Matches(plan, @"\b(handle|manage|process|deal with)\b",
                RegexOptions.IgnoreCase).Count;
            if (vague >= 5 && numbered >= 3)
                return "Plan is too vague — replace handle/manage/process with exact operations.";

            return string.Empty;
        }

        public static CriticReport EnforceCriticEvidence(CriticReport report, bool codingTask, bool hadSandboxOrStatic)
        {
            report ??= new CriticReport { Status = "issues", Issues = new List<CriticIssue>() };
            report.Issues ??= new List<CriticIssue>();

            // Issues without evidence get a placeholder requirement
            foreach (var issue in report.Issues)
            {
                if (string.IsNullOrWhiteSpace(issue.Evidence)
                    || issue.Evidence.Equals("n/a", StringComparison.OrdinalIgnoreCase)
                    || issue.Evidence.Length < 4)
                {
                    issue.Evidence = string.IsNullOrWhiteSpace(issue.Evidence)
                        ? "(missing file:line or test output — required)"
                        : issue.Evidence;
                    if (!issue.Summary.Contains("evidence", StringComparison.OrdinalIgnoreCase))
                        issue.Summary = issue.Summary + " [needs stronger evidence]";
                }
            }

            bool clean = string.Equals(report.Status, "ok", StringComparison.OrdinalIgnoreCase)
                && report.Issues.Count == 0;

            if (clean && codingTask && !hadSandboxOrStatic)
            {
                // Soft note: do not hard-fail every clean pass without tools — only when clearly empty of any path citation
                // The raw critic text is not available here; leave clean if parser said ok.
            }

            // Reject pure "looks fine" clean pass when issues claimed empty but summary-like status wrong
            report.HasIssues = report.Issues.Count > 0;
            report.FindingsCount = report.Issues.Count;
            if (report.Issues.Count > 0)
                report.Status = "issues";
            return report;
        }

        public static bool CriticOutputLacksEvidence(string criticOutput, CriticReport report)
        {
            if (report.Issues is { Count: > 0 })
            {
                return report.Issues.Any(i =>
                    string.IsNullOrWhiteSpace(i.Evidence)
                    || i.Evidence.Contains("missing file:line", StringComparison.OrdinalIgnoreCase));
            }

            // Clean pass without any path-like citation in the raw text for coding reviews is weak
            if (string.Equals(report.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                bool hasCite = Regex.IsMatch(criticOutput ?? "",
                    @"[\w./\\-]+\.\w{1,8}:\d+|\bexit_code:|\btest(s)?\b.*(pass|fail)|\[\[DIAGNOSTICS\]\]",
                    RegexOptions.IgnoreCase);
                bool looksFine = Regex.IsMatch(criticOutput ?? "",
                    @"looks?\s+(good|fine)|no issues|lgtm|seems correct",
                    RegexOptions.IgnoreCase);
                return looksFine && !hasCite;
            }
            return false;
        }

        public static string FewShotFromHistory(IReadOnlyList<OpenRouterMessage> history, string userPrompt, int max = 2)
        {
            if (history == null || history.Count == 0)
                return string.Empty;

            var keywords = Regex.Matches(userPrompt ?? "", @"[A-Za-z_]{4,}")
                .Select(m => m.Value.ToLowerInvariant())
                .Distinct()
                .Take(12)
                .ToHashSet();

            var candidates = new List<(int Score, string Block)>();
            for (int i = 0; i < history.Count - 1; i++)
            {
                if (history[i].Role != "user" || history[i + 1].Role != "assistant")
                    continue;
                string u = history[i].Text ?? "";
                string a = history[i + 1].Text ?? "";
                if (u.Length < 20 || a.Length < 40)
                    continue;
                // Prefer turns that mention files/edits
                if (!Regex.IsMatch(a, @"\b(wrote|patched|updated|created|fixed)\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(a, @"\.\w{1,5}\b"))
                    continue;

                int score = keywords.Count(k => u.Contains(k, StringComparison.OrdinalIgnoreCase)
                    || a.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (score == 0)
                    continue;

                string ub = u.Length > 160 ? u[..157] + "…" : u;
                string ab = a.Length > 280 ? a[..277] + "…" : a;
                candidates.Add((score, $"User: {ub}\nAssistant: {ab}"));
            }

            if (candidates.Count == 0)
                return string.Empty;

            var top = candidates.OrderByDescending(c => c.Score).Take(max).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("[[FEW-SHOT FROM SESSION]]");
            sb.AppendLine("Similar past turns in this session (style/approach only — follow the NEW request):");
            int n = 1;
            foreach (var c in top)
            {
                sb.AppendLine($"Example {n++}:");
                sb.AppendLine(c.Block);
            }
            sb.AppendLine("[[END FEW-SHOT]]");
            return sb.ToString();
        }

        public static string BuildSpecMarkdown(IReadOnlyList<OpenRouterMessage> history, string? title = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + (string.IsNullOrWhiteSpace(title) ? "SPEC" : title.Trim()));
            sb.AppendLine();
            sb.AppendLine($"_Generated by Axiom CLI on {DateTime.Now:yyyy-MM-dd HH:mm}_");
            sb.AppendLine();
            sb.AppendLine("## Goals");
            sb.AppendLine();
            var goals = history.Where(m => m.Role == "user")
                .Select(m => (m.Text ?? "").Trim())
                .Where(c => c.Length > 0 && !c.StartsWith('[') && !c.StartsWith('/'))
                .TakeLast(12)
                .ToList();
            if (goals.Count == 0)
                sb.AppendLine("- (no user goals found in session)");
            else
            {
                int i = 1;
                foreach (string g in goals)
                {
                    string line = g.Replace("\r", " ").Replace("\n", " ");
                    if (line.Length > 240)
                        line = line[..237] + "…";
                    sb.AppendLine($"{i++}. {line}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Decisions / notes from assistant");
            sb.AppendLine();
            var notes = history.Where(m => m.Role == "assistant")
                .Select(m => (m.Text ?? "").Trim())
                .Where(c => c.Length > 40)
                .TakeLast(6)
                .ToList();
            if (notes.Count == 0)
                sb.AppendLine("- (none)");
            else
            {
                foreach (string n in notes)
                {
                    string line = n.Replace("\r", " ").Replace("\n", " ");
                    if (line.Length > 300)
                        line = line[..297] + "…";
                    sb.AppendLine("- " + line);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Acceptance criteria");
            sb.AppendLine();
            sb.AppendLine("- [ ] Core request satisfied");
            sb.AppendLine("- [ ] Relevant tests pass");
            sb.AppendLine("- [ ] No unrelated drive-by changes");
            sb.AppendLine();
            sb.AppendLine("## Implementation plan");
            sb.AppendLine();
            sb.AppendLine("1. …");
            sb.AppendLine("2. …");
            return sb.ToString();
        }

        public static string DualPassInstruction =>
            "[[DUAL-PASS]] For pure Q&A (no edits): draft briefly, self-check for mistakes, then give the final answer only. " +
            "Do not show the draft. [[END DUAL-PASS]]";
    }
}
