using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Axiom.Core.Workspace;

namespace Axiom.Core.Council
{
    // Built from the verified shape of WorkplaceView.SendQueryAsync (Architect → Builder →
    // Critic, confidence-routed revision) plus the pre-Critic rails the desktop app always runs:
    // static validation, optional Python/Java sandbox execution, and injection of those findings
    // into the Critic payload as PRE-FLAGGED ISSUES / SANDBOX OUTPUT.
    public sealed class CouncilOrchestrator
    {
        // Matches WorkplaceView.xaml.cs MaxBuilderRetryAttempts.
        private const int MaxBuilderRetryAttempts = 2;
        private const int TargetedPatchIssueCeiling = 2; // 1-2 issues => targeted patch
        private const int WorkspaceContextBudgetChars = 60_000;
        private const int SandboxOutputCapChars = 16_000;
        private const int BuilderForCriticCapChars = 96_000;

        private readonly IChatPipeline _pipeline;
        private readonly string _modelId;
        private readonly WorkspaceAccessService _workspace;
        private readonly CouncilCodeSandbox _sandbox;

        public CouncilOrchestrator(
            IChatPipeline pipeline,
            string modelId,
            WorkspaceAccessService? workspace = null,
            CouncilCodeSandbox? sandbox = null)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _modelId = modelId;
            _workspace = workspace ?? new WorkspaceAccessService();
            _sandbox = sandbox ?? new CouncilCodeSandbox();
        }

        public async Task<CouncilResult> RunAsync(
            CouncilRequest request,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            CouncilToolOptions tools = request.Tools ?? CouncilToolOptions.Default;
            bool isWorkspaceTask = request.Workspace is { CodebaseEditAccessEnabled: true };
            string workspaceContext = string.Empty;
            IReadOnlyList<string> workspaceFilesRead = Array.Empty<string>();

            if (isWorkspaceTask)
            {
                WorkspaceContextResult context = _workspace.BuildContextPacket(
                    request.Workspace!, request.UserPrompt, WorkspaceContextBudgetChars);
                workspaceContext = context.Packet;
                workspaceFilesRead = context.FilesRead;
                Report(progress, CouncilEventKind.Status,
                    workspaceFilesRead.Count > 0
                        ? $"Connected codebase context attached: {workspaceFilesRead.Count} file(s)."
                        : "Codebase Edit Access is enabled, but no readable local code files are connected yet.");
            }

            // ── Architect: plan ────────────────────────────────────────────────
            Report(progress, CouncilEventKind.Status, "Architect is planning...");
            string architectSystem = FoundationSystemPrompt.Apply(
                "You are the Architect. Read the user's request and any attached context, then " +
                "produce a concise numbered implementation plan (3-8 steps). Do not write final " +
                "code or prose output — only the plan.");
            string architectInput = BuildContextualInput(request.UserPrompt, workspaceContext);
            string architectPlan = await CallRoleAsync(architectSystem, architectInput, progress, cancellationToken);
            Report(progress, CouncilEventKind.ArchitectOutput, architectPlan);

            // ── Builder: implement ──────────────────────────────────────────────
            string builderSystem = BuildBuilderSystemPrompt(isWorkspaceTask);
            string builderInput = BuildBuilderInput(request.UserPrompt, architectPlan, workspaceContext);

            Report(progress, CouncilEventKind.Status, "Builder is implementing...");
            string builderOutput = await CallRoleAsync(builderSystem, builderInput, progress, cancellationToken);
            Report(progress, CouncilEventKind.BuilderOutput, builderOutput);

            WorkspacePatchProposal? patch = null;
            if (isWorkspaceTask)
            {
                patch = await ResolvePatchWithRetryAsync(
                    request, workspaceContext, architectPlan, builderSystem, builderOutput, progress, cancellationToken);
                if (patch == null)
                {
                    return new CouncilResult(
                        Success: false,
                        FinalText: builderOutput,
                        Patch: null,
                        FinalCriticReport: new CriticReport { Status = "issues" });
                }

                builderOutput = patch.RawText ?? builderOutput;
            }

            // ── Critic: static validation + optional sandbox + review/revision ─
            int retries = 0;
            CriticReport report;
            while (true)
            {
                // Deterministic rails before every Critic call (desktop Stage 2.5 / sandbox).
                CriticEvidence evidence = await GatherCriticEvidenceAsync(
                    builderOutput, isWorkspaceTask, tools, progress, cancellationToken);

                Report(progress, CouncilEventKind.Status, "Critic is reviewing...");
                string criticInput = BuildCriticInput(request.UserPrompt, isWorkspaceTask, builderOutput, evidence);
                string criticOutput = await CallRoleAsync(CriticSystemPrompt, criticInput, progress, cancellationToken);
                Report(progress, CouncilEventKind.CriticOutput, criticOutput);
                report = CriticContractParser.Parse(criticOutput);

                // Deterministic findings force an issues status even if the LLM clean-passes.
                report = MergeDeterministicFindings(report, evidence);

                int issueCount = report.Issues?.Count ?? 0;
                if (issueCount == 0)
                {
                    Report(progress, CouncilEventKind.Status, "Critic found no issues.");
                    break;
                }

                if (retries >= MaxBuilderRetryAttempts)
                {
                    Report(progress, CouncilEventKind.Warning,
                        $"Builder retry limit reached ({MaxBuilderRetryAttempts}); keeping current output. " +
                        $"{issueCount} Critic finding(s) remain.");
                    break;
                }

                retries++;
                bool fullRevision = issueCount > TargetedPatchIssueCeiling
                    || evidence.SandboxFailed;
                Report(progress, CouncilEventKind.Status,
                    fullRevision
                        ? $"Critic found {issueCount} issues — running full revision ({retries}/{MaxBuilderRetryAttempts})..."
                        : $"Critic found {issueCount} issue(s) — running targeted patch ({retries}/{MaxBuilderRetryAttempts})...");

                string revisionSystem = fullRevision
                    ? builderSystem
                    : builderSystem + "\n" + TargetedPatchModeNote(isWorkspaceTask);
                string revisionInput = BuildRevisionInput(
                    request.UserPrompt, architectPlan, workspaceContext, builderOutput, criticOutput, fullRevision, evidence);

                builderOutput = await CallRoleAsync(revisionSystem, revisionInput, progress, cancellationToken);
                Report(progress, CouncilEventKind.BuilderOutput, builderOutput);

                if (isWorkspaceTask)
                {
                    patch = await ResolvePatchWithRetryAsync(
                        request, workspaceContext, architectPlan, builderSystem, builderOutput, progress, cancellationToken);
                    if (patch == null)
                        return new CouncilResult(false, builderOutput, null, report);

                    builderOutput = patch.RawText ?? builderOutput;
                }
            }

            Report(progress, CouncilEventKind.Completed, "Council run complete.");
            return new CouncilResult(true, builderOutput, patch, report);
        }

        private async Task<CriticEvidence> GatherCriticEvidenceAsync(
            string builderOutput,
            bool isWorkspaceTask,
            CouncilToolOptions tools,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            var findings = new List<string>();
            string sandboxLogs = string.Empty;
            bool sandboxFailed = false;
            string language = StaticValidation.DetectLanguage(builderOutput);

            // Always run deterministic checks on Builder output (GUI Stage 2.5). Pure prose
            // yields no findings; structure/call checks only fire when the text looks code-like.
            // Workspace patches are analyzed via REPLACE bodies inside StaticValidation.Run.
            if (!string.IsNullOrWhiteSpace(builderOutput))
            {
                Report(progress, CouncilEventKind.Status, "Running static validation...");
                findings.AddRange(StaticValidation.Run(builderOutput));
                if (findings.Count > 0)
                {
                    Report(progress, CouncilEventKind.Status,
                        $"Static validation: {findings.Count} issue(s) pre-flagged for Critic.");
                }
            }

            bool runSandbox = tools.SandboxEnabled
                && !isWorkspaceTask
                && StaticValidation.IsSandboxLanguage(language)
                && !string.IsNullOrWhiteSpace(builderOutput);

            if (runSandbox)
            {
                Report(progress, CouncilEventKind.Status, $"Running {language} sandbox...");
                CouncilSandboxResult sandbox = await _sandbox.ExecuteAsync(builderOutput, language, cancellationToken);
                if (sandbox.HasOutput)
                {
                    sandboxLogs = sandbox.Output;
                    var runtimeErrors = StaticValidation.DetectSandboxErrors(sandbox.Output);
                    if (runtimeErrors.Count > 0 || !sandbox.Succeeded || sandbox.TimedOut)
                    {
                        sandboxFailed = true;
                        findings.AddRange(runtimeErrors);
                        if (runtimeErrors.Count == 0)
                            findings.Add("[CRITICAL — RUNTIME ERROR] Sandbox execution failed without a parseable stack trace.");
                        Report(progress, CouncilEventKind.Warning,
                            $"Sandbox: {runtimeErrors.Count} runtime issue(s) pre-flagged for Critic.");
                    }
                    else
                    {
                        Report(progress, CouncilEventKind.Status, "Sandbox execution succeeded.");
                    }
                }
            }

            findings = findings.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
            return new CriticEvidence(findings, sandboxLogs, sandboxFailed, language);
        }

        private static CriticReport MergeDeterministicFindings(CriticReport report, CriticEvidence evidence)
        {
            report.Issues ??= new List<CriticIssue>();

            // When the LLM says ok but static/sandbox found hard failures, promote to issues.
            var hardFindings = evidence.Findings
                .Where(f => f.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)
                    || f.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase)
                    || f.Contains("Mismatched", StringComparison.OrdinalIgnoreCase)
                    || f.Contains("truncated", StringComparison.OrdinalIgnoreCase)
                    || f.Contains("not defined", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (hardFindings.Count == 0)
            {
                // Soft static notes stay as evidence in the prompt; only hard ones force status.
                report.FindingsCount = report.Issues.Count;
                report.HasIssues = report.Issues.Count > 0
                    && !string.Equals(report.Status, "ok", StringComparison.OrdinalIgnoreCase);
                return report;
            }

            var existing = new HashSet<string>(
                report.Issues.Select(i => i.Summary ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            foreach (string finding in hardFindings)
            {
                string summary = finding.Trim();
                if (summary.StartsWith("RUNTIME:", StringComparison.OrdinalIgnoreCase))
                    summary = summary["RUNTIME:".Length..].Trim();
                if (existing.Contains(summary))
                    continue;

                report.Issues.Add(new CriticIssue
                {
                    Severity = finding.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase) || finding.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase)
                        ? "critical"
                        : "high",
                    Summary = summary.Length > 240 ? summary[..240] : summary,
                    Evidence = "Deterministic static validation / sandbox",
                    SuggestedFix = "Fix the reported structural or runtime failure before shipping."
                });
                existing.Add(summary);
            }

            report.Status = "issues";
            report.FindingsCount = report.Issues.Count;
            report.HasIssues = report.Issues.Count > 0;
            return report;
        }

        private async Task<WorkspacePatchProposal?> ResolvePatchWithRetryAsync(
            CouncilRequest request,
            string workspaceContext,
            string architectPlan,
            string builderSystem,
            string builderOutput,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt <= 1; attempt++)
            {
                if (_workspace.TryParsePatchProposal(builderOutput, out WorkspacePatchProposal? proposal, out string error))
                    return proposal;

                if (attempt == 1)
                {
                    Report(progress, CouncilEventKind.Failed, $"Builder did not return a valid patch: {error}");
                    return null;
                }

                Report(progress, CouncilEventKind.Warning, $"Patch did not parse ({error}); asking Builder to correct the format.");
                string retryInput = BuildBuilderInput(request.UserPrompt, architectPlan, workspaceContext)
                    + "\n\n[FORMAT CORRECTION NEEDED]\nYour previous output did not parse as a valid " +
                    "[[AXIOM_CODEBASE_PATCH]] envelope: " + error +
                    "\nOutput ONLY a corrected, valid patch envelope now.";
                builderOutput = await CallRoleAsync(builderSystem, retryInput, progress, cancellationToken);
                Report(progress, CouncilEventKind.BuilderOutput, builderOutput);
            }

            return null;
        }

        private async Task<string> CallRoleAsync(
            string systemPrompt,
            string userInput,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            var request = new ChatPipelineRequest(systemPrompt, userInput);
            ChatPipelineResult result = await _pipeline.ExecuteAsync(request, onToken: null, cancellationToken);
            return result.ResponseText;
        }

        private static string BuildBuilderSystemPrompt(bool isWorkspaceTask)
        {
            string role = "You are the Builder. Implement the Architect's plan completely — no " +
                "TODO placeholders, no omitted sections, no pseudo-code.";
            return FoundationSystemPrompt.Apply(isWorkspaceTask
                ? role + "\n" + WorkspacePatchModeNote
                : role);
        }

        private const string WorkspacePatchModeNote =
            "This is a connected-codebase task. Follow the patch envelope format given in the " +
            "workspace context exactly. Output ONLY the [[AXIOM_CODEBASE_PATCH]] envelope — no " +
            "narration before or after it.";

        private static string TargetedPatchModeNote(bool isWorkspaceTask) => isWorkspaceTask
            ? "[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. Output a complete, " +
              "valid [[AXIOM_CODEBASE_PATCH]] envelope — do not output a diff or partial snippet, " +
              "and do not change anything not mentioned in the findings."
            : "[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. Output the complete " +
              "corrected response and preserve unaffected content. Do not output a diff.";

        private static string CriticSystemPrompt => FoundationSystemPrompt.Apply(
            "You are the Critic. Review the Builder's output against the original request for " +
            "correctness, completeness, and whether it actually implements what was asked. " +
            "Be specific and evidence-based. When PRE-FLAGGED ISSUES or SANDBOX OUTPUT are " +
            "present, confirm each finding and check for additional problems. Treat CRITICAL " +
            "and RUNTIME findings as must-fix unless the sandbox output clearly shows they " +
            "are false positives." + "\n" + CriticContractParser.ContractInstruction);

        private static string BuildContextualInput(string userPrompt, string workspaceContext)
        {
            return string.IsNullOrWhiteSpace(workspaceContext)
                ? userPrompt
                : userPrompt + "\n\n" + workspaceContext;
        }

        private static string BuildBuilderInput(string userPrompt, string architectPlan, string workspaceContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ORIGINAL REQUEST]").AppendLine(userPrompt).AppendLine();
            sb.AppendLine("[ARCHITECT PLAN]").AppendLine(architectPlan);
            if (!string.IsNullOrWhiteSpace(workspaceContext))
                sb.AppendLine().AppendLine(workspaceContext);
            return sb.ToString();
        }

        private static string BuildCriticInput(
            string userPrompt,
            bool isWorkspaceTask,
            string builderOutput,
            CriticEvidence evidence)
        {
            var sb = new StringBuilder();

            if (evidence.Findings.Count > 0)
            {
                sb.AppendLine("[PRE-FLAGGED ISSUES]");
                sb.AppendLine("The following issues were detected automatically. Confirm each one and check for additional problems:");
                for (int i = 0; i < evidence.Findings.Count; i++)
                    sb.AppendLine($"{i + 1}. {evidence.Findings[i]}");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(evidence.SandboxLogs))
            {
                string sandbox = evidence.SandboxLogs.Length > SandboxOutputCapChars
                    ? evidence.SandboxLogs[..SandboxOutputCapChars] + "\n[...truncated]"
                    : evidence.SandboxLogs;
                sb.AppendLine("[SANDBOX OUTPUT]").AppendLine(sandbox).AppendLine();
            }

            sb.AppendLine("[ORIGINAL REQUEST]").AppendLine(userPrompt).AppendLine();
            sb.AppendLine(isWorkspaceTask ? "[BUILDER PATCH PROPOSAL]" : "[BUILDER OUTPUT]");
            string builderText = builderOutput;
            if (builderText.Length > BuilderForCriticCapChars)
            {
                builderText = builderText[..64_000]
                    + "\n[...middle section truncated for context budget...]\n"
                    + builderText[^32_000..];
            }

            sb.AppendLine(builderText);
            return sb.ToString();
        }

        private static string BuildRevisionInput(
            string userPrompt,
            string architectPlan,
            string workspaceContext,
            string builderOutput,
            string criticOutput,
            bool fullRevision,
            CriticEvidence evidence)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ORIGINAL REQUEST]").AppendLine(userPrompt).AppendLine();
            if (fullRevision)
                sb.AppendLine("[ARCHITECT PLAN]").AppendLine(architectPlan).AppendLine();
            sb.AppendLine("[PREVIOUS BUILDER OUTPUT]").AppendLine(builderOutput).AppendLine();
            sb.AppendLine("[CRITIC FINDINGS]").AppendLine(criticOutput);

            if (evidence.Findings.Count > 0)
            {
                sb.AppendLine().AppendLine("[PRE-FLAGGED ISSUES — must address]");
                foreach (string f in evidence.Findings)
                    sb.AppendLine("- " + f);
            }

            if (!string.IsNullOrWhiteSpace(evidence.SandboxLogs))
            {
                string sandbox = evidence.SandboxLogs.Length > 4000
                    ? evidence.SandboxLogs[..4000] + "\n[...truncated]"
                    : evidence.SandboxLogs;
                sb.AppendLine().AppendLine("[SANDBOX OUTPUT]").AppendLine(sandbox);
            }

            if (!string.IsNullOrWhiteSpace(workspaceContext))
                sb.AppendLine().AppendLine(workspaceContext);
            return sb.ToString();
        }

        private static void Report(IProgress<CouncilEvent>? progress, CouncilEventKind kind, string message)
            => progress?.Report(new CouncilEvent(kind, message));

        private sealed record CriticEvidence(
            IReadOnlyList<string> Findings,
            string SandboxLogs,
            bool SandboxFailed,
            string Language);
    }
}
