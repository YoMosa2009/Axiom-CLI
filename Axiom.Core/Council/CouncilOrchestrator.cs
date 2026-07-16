using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Workspace;

namespace Axiom.Core.Council
{
    // Built from the verified shape of WorkplaceView.SendQueryAsync (Architect → Builder →
    // Critic, confidence-routed revision) plus the pre-Critic rails the desktop app always runs:
    // static validation, optional Python/Java sandbox execution, and injection of those findings
    // into the Critic payload as PRE-FLAGGED ISSUES / SANDBOX OUTPUT.
    //
    // When OpenRouterChatService + AgentToolExecutor are wired (CLI chat), the Builder runs an
    // agentic tool loop (write_file, run_shell, list_dir, …) so work lands on disk — not only
    // as terminal text — matching Claude Code / Codex-style coding agents.
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
        private readonly OpenRouterChatService? _chat;
        private readonly AgentToolExecutor? _agentTools;

        public CouncilOrchestrator(
            IChatPipeline pipeline,
            string modelId,
            WorkspaceAccessService? workspace = null,
            CouncilCodeSandbox? sandbox = null,
            OpenRouterChatService? chat = null,
            AgentToolExecutor? agentTools = null)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _modelId = modelId;
            _workspace = workspace ?? new WorkspaceAccessService();
            _sandbox = sandbox ?? new CouncilCodeSandbox();
            _chat = chat;
            _agentTools = agentTools;
        }

        private bool CanRunAgenticBuilder(CouncilToolOptions tools)
            => tools.AgenticBuilderEnabled
               && _chat != null
               && _agentTools != null;

        public async Task<CouncilResult> RunAsync(
            CouncilRequest request,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            CouncilToolOptions tools = request.Tools ?? CouncilToolOptions.Default;
            bool agentic = CanRunAgenticBuilder(tools);
            // Folder connected for context (index + file contents). Patch-only mode is separate:
            // Q&A / explore turns must still answer from the connected tree, not claim "no access".
            bool workspaceConnected = request.Workspace is { CodebaseEditAccessEnabled: true };
            bool looksLikeEdit = LooksLikeCodeEditRequest(request.UserPrompt);
            // With agentic tools, edits go through write_file/shell — do not fail the turn if no
            // patch envelope is produced. Without tools, edit turns still require a patch proposal.
            bool expectPatch = workspaceConnected && looksLikeEdit && !agentic;
            string workspaceContext = string.Empty;
            IReadOnlyList<string> workspaceFilesRead = Array.Empty<string>();
            int totalToolCalls = 0;
            var changedFiles = new List<string>();
            string? applySummary = null;

            if (workspaceConnected)
            {
                WorkspaceContextResult context = _workspace.BuildContextPacket(
                    request.Workspace!, request.UserPrompt, WorkspaceContextBudgetChars);
                workspaceContext = context.Packet;
                workspaceFilesRead = context.FilesRead;
                bool hasAccessNotice = workspaceContext.Contains("YOU HAVE ACCESS", StringComparison.Ordinal);
                Report(progress, CouncilEventKind.Status,
                    workspaceFilesRead.Count > 0
                        ? $"Workspace connected · {workspaceFilesRead.Count} file content(s) in context."
                        : hasAccessNotice
                            ? "Workspace connected · file index attached (no content slices yet)."
                            : "Workspace flag set, but no local folder path was available.");
            }

            if (agentic)
                Report(progress, CouncilEventKind.Status, "Council · Builder has agentic tools (files/shell).");

            // ── Architect: plan ────────────────────────────────────────────────
            Report(progress, CouncilEventKind.Status, "Architect is planning...");
            string architectSystem = FoundationSystemPrompt.Apply(
                "You are the Architect. Read the user's request and any attached workspace context, then " +
                "produce a concise numbered plan (3-8 steps). Do not write final code or long prose — only the plan.\n" +
                "If [[CONNECTED WORKSPACE — YOU HAVE ACCESS]] is present, the user's local project IS connected: " +
                "plan using the FILE INDEX and RELEVANT FILE CONTENTS. Never claim you lack access to their files.\n" +
                (agentic
                    ? "The Builder has agentic tools (write_file, run_shell, list_dir, read_file, search_files). " +
                      "Plan concrete file/shell steps the Builder should execute on disk."
                    : string.Empty));
            string architectInput = BuildContextualInput(request.UserPrompt, workspaceContext);
            string architectPlan = await CallRoleAsync(architectSystem, architectInput, progress, cancellationToken);
            Report(progress, CouncilEventKind.ArchitectOutput, architectPlan);

            // ── Builder: implement (agentic tool loop when available) ──────────
            string builderSystem = BuildBuilderSystemPrompt(workspaceConnected, expectPatch, agentic, looksLikeEdit);
            string builderInput = BuildBuilderInput(request.UserPrompt, architectPlan, workspaceContext);

            Report(progress, CouncilEventKind.Status,
                agentic ? "Builder is implementing with tools..." : "Builder is implementing...");
            _agentTools?.ClearWrittenPaths();
            (string builderOutput, int builderTools) = await CallBuilderAsync(
                builderSystem, builderInput, agentic, progress, cancellationToken);
            totalToolCalls += builderTools;
            CollectWrittenFiles(changedFiles);
            Report(progress, CouncilEventKind.BuilderOutput, builderOutput);

            WorkspacePatchProposal? patch = null;
            bool builderEmittedPatch = ContainsPatchEnvelope(builderOutput);
            if (expectPatch || builderEmittedPatch)
            {
                patch = await ResolvePatchWithRetryAsync(
                    request, workspaceContext, architectPlan, builderSystem, builderOutput, progress, cancellationToken,
                    agentic);
                if (patch == null && expectPatch)
                {
                    return new CouncilResult(
                        Success: false,
                        FinalText: builderOutput,
                        Patch: null,
                        FinalCriticReport: new CriticReport { Status = "issues" },
                        ToolCallCount: totalToolCalls);
                }

                if (patch != null)
                    builderOutput = patch.RawText ?? builderOutput;
            }

            // ── Critic: static validation + optional sandbox + review/revision ─
            int retries = 0;
            CriticReport report;
            while (true)
            {
                // Deterministic rails before every Critic call (desktop Stage 2.5 / sandbox).
                CriticEvidence evidence = await GatherCriticEvidenceAsync(
                    builderOutput, expectPatch || patch != null, tools, progress, cancellationToken);

                Report(progress, CouncilEventKind.Status, "Critic is reviewing...");
                string criticInput = BuildCriticInput(request.UserPrompt, patch != null, builderOutput, evidence);
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
                    : builderSystem + "\n" + TargetedPatchModeNote(expectPatch || patch != null, agentic);
                string revisionInput = BuildRevisionInput(
                    request.UserPrompt, architectPlan, workspaceContext, builderOutput, criticOutput, fullRevision, evidence);

                (builderOutput, int revisionTools) = await CallBuilderAsync(
                    revisionSystem, revisionInput, agentic, progress, cancellationToken);
                totalToolCalls += revisionTools;
                CollectWrittenFiles(changedFiles);
                Report(progress, CouncilEventKind.BuilderOutput, builderOutput);

                if (expectPatch || ContainsPatchEnvelope(builderOutput))
                {
                    patch = await ResolvePatchWithRetryAsync(
                        request, workspaceContext, architectPlan, builderSystem, builderOutput, progress, cancellationToken,
                        agentic);
                    if (patch == null && expectPatch)
                        return new CouncilResult(false, builderOutput, null, report, totalToolCalls);

                    if (patch != null)
                        builderOutput = patch.RawText ?? builderOutput;
                }
            }

            // Apply structured patch when the host requested auto-apply (chat). axiom code keeps
            // interactive confirm; agentic write_file already landed files during the Builder loop.
            if (patch != null
                && workspaceConnected
                && request.Workspace is { AutoApplyCodebaseChanges: true })
            {
                try
                {
                    Report(progress, CouncilEventKind.Status, "Applying codebase patch to workspace...");
                    WorkspacePatchApplyResult applied = _workspace.ApplyPatchProposal(request.Workspace, patch);
                    applySummary = applied.Summary;
                    changedFiles.AddRange(applied.ChangedFiles);
                    foreach (string f in applied.ChangedFiles.Reverse())
                    {
                        request.Workspace.RecentlyChangedFiles.RemoveAll(x =>
                            string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
                        request.Workspace.RecentlyChangedFiles.Insert(0, f);
                    }
                    Report(progress, CouncilEventKind.Status,
                        $"Applied patch · {applied.ChangedFiles.Count} file(s) written.");
                }
                catch (Exception ex)
                {
                    Report(progress, CouncilEventKind.Warning, "Patch apply failed: " + ex.Message);
                    applySummary = "Patch apply failed: " + ex.Message;
                }
            }

            Report(progress, CouncilEventKind.Completed,
                totalToolCalls > 0
                    ? $"Council run complete · {totalToolCalls} tool call(s)."
                    : "Council run complete.");
            return new CouncilResult(
                true,
                builderOutput,
                patch,
                report,
                totalToolCalls,
                changedFiles.Count > 0 ? changedFiles : null,
                applySummary);
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
            CancellationToken cancellationToken,
            bool agentic = false)
        {
            for (int attempt = 0; attempt <= 1; attempt++)
            {
                if (_workspace.TryParsePatchProposal(builderOutput, out WorkspacePatchProposal? proposal, out string error))
                    return proposal;

                if (attempt == 1)
                {
                    // Agentic Builder may have already written files via tools — missing patch is OK.
                    if (!agentic)
                        Report(progress, CouncilEventKind.Failed, $"Builder did not return a valid patch: {error}");
                    return null;
                }

                Report(progress, CouncilEventKind.Warning, $"Patch did not parse ({error}); asking Builder to correct the format.");
                string retryInput = BuildBuilderInput(request.UserPrompt, architectPlan, workspaceContext)
                    + "\n\n[FORMAT CORRECTION NEEDED]\nYour previous output did not parse as a valid " +
                    "[[AXIOM_CODEBASE_PATCH]] envelope: " + error +
                    (agentic
                        ? "\nEither emit a corrected patch envelope OR use write_file tools to apply the edits on disk, then summarize."
                        : "\nOutput ONLY a corrected, valid patch envelope now.");
                (builderOutput, _) = await CallBuilderAsync(builderSystem, retryInput, agentic, progress, cancellationToken);
                Report(progress, CouncilEventKind.BuilderOutput, builderOutput);
            }

            return null;
        }

        private async Task<(string Text, int ToolCalls)> CallBuilderAsync(
            string systemPrompt,
            string userInput,
            bool agentic,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            if (!agentic || _chat == null || _agentTools == null)
            {
                string text = await CallRoleAsync(systemPrompt, userInput, progress, cancellationToken);
                return (text, 0);
            }

            var loop = new ToolCallingLoop(_chat, _agentTools, _modelId);
            ToolCallingResult result = await loop.RunAsync(
                systemPrompt,
                userInput,
                status => Report(progress, CouncilEventKind.Status, "Builder · " + status),
                cancellationToken);

            if (result.ToolCallCount > 0)
            {
                Report(progress, CouncilEventKind.Status,
                    $"Builder · {result.ToolCallCount} tool call(s) completed.");
            }

            return (result.FinalText, result.ToolCallCount);
        }

        private void CollectWrittenFiles(List<string> changedFiles)
        {
            if (_agentTools == null)
                return;

            foreach (string path in _agentTools.WrittenPaths)
            {
                if (!changedFiles.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                    changedFiles.Add(path);
            }
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

        private static string BuildBuilderSystemPrompt(
            bool workspaceConnected,
            bool expectPatch,
            bool agentic,
            bool looksLikeEdit)
        {
            string role = "You are the Builder. Implement the Architect's plan completely — no " +
                "TODO placeholders, no omitted sections, no pseudo-code.";

            if (agentic)
            {
                string agenticNote = AgenticBuilderNote;
                if (workspaceConnected)
                    agenticNote += "\n" + WorkspaceAccessNote;
                if (looksLikeEdit)
                    agenticNote += "\n" + AgenticEditModeNote;
                else if (workspaceConnected)
                    agenticNote += "\n" + WorkspaceAnswerModeNote;
                return FoundationSystemPrompt.Apply(role + "\n" + agenticNote);
            }

            if (workspaceConnected && expectPatch)
                return FoundationSystemPrompt.Apply(role + "\n" + WorkspaceAccessNote + "\n" + WorkspacePatchModeNote);

            if (workspaceConnected)
                return FoundationSystemPrompt.Apply(role + "\n" + WorkspaceAccessNote + "\n" + WorkspaceAnswerModeNote);

            return FoundationSystemPrompt.Apply(role);
        }

        private const string AgenticBuilderNote =
            "[AGENTIC TOOLS] You have real tools: write_file, read_file, list_dir, search_files, " +
            "run_shell, download_file, and web_search (when enabled).\n" +
            "You MUST use these tools to create and edit files on disk when the plan requires " +
            "implementation. Do NOT only paste full file contents into the chat as your final " +
            "answer — call write_file (or run_shell) so the user's workspace actually changes.\n" +
            "Workflow: list_dir/read_file to inspect → write_file/run_shell to implement → " +
            "optionally run_shell for build/test → then summarize what you changed and how to run it.";

        private const string AgenticEditModeNote =
            "This turn expects real codebase changes. Prefer write_file / run_shell to apply them. " +
            "You may ALSO emit a [[AXIOM_CODEBASE_PATCH]] envelope after tools as a structured " +
            "summary of edits (optional when tools already wrote the files).";

        private const string WorkspaceAccessNote =
            "A local workspace is connected for this turn (see FILE INDEX / RELEVANT FILE CONTENTS). " +
            "You HAVE access to those files. Never claim you cannot open the project or lack filesystem access.";

        private const string WorkspacePatchModeNote =
            "This turn expects codebase edits. Follow the patch envelope format in the workspace " +
            "context exactly. Output ONLY the [[AXIOM_CODEBASE_PATCH]] envelope — no narration " +
            "before or after it.";

        private const string WorkspaceAnswerModeNote =
            "This turn is Q&A / analysis / exploration (not a forced edit). Answer in clear prose " +
            "using the connected FILE INDEX and RELEVANT FILE CONTENTS (and tools if available). " +
            "If the user also wants code changes, use write_file tools and/or a " +
            "[[AXIOM_CODEBASE_PATCH]] envelope after your explanation.";

        private static string TargetedPatchModeNote(bool patchMode, bool agentic) => agentic
            ? "[TARGETED FIX MODE] Fix ONLY the specific issues listed below using tools " +
              "(write_file / run_shell). Then summarize. Do not change unrelated files."
            : patchMode
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
            "are false positives. If a connected workspace was provided, do not mark the Builder " +
            "wrong for using those files — they were intentionally attached." + "\n" +
            CriticContractParser.ContractInstruction);

        /// <summary>
        /// Heuristic: edit-like prompts require a patch envelope; explore/Q&A do not.
        /// </summary>
        public static bool LooksLikeCodeEditRequest(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return false;

            string p = prompt.ToLowerInvariant();
            // Strip pre-injected tool blocks so "search" in web results doesn't force patch mode.
            int cut = p.IndexOf("[[web search", StringComparison.Ordinal);
            if (cut < 0) cut = p.IndexOf("[[python sandbox", StringComparison.Ordinal);
            if (cut < 0) cut = p.IndexOf("[[calculator", StringComparison.Ordinal);
            if (cut > 0)
                p = p[..cut];

            string[] editHints =
            [
                "fix", "implement", "refactor", "rename", "migrate", "upgrade",
                "patch", "wire up", "integrate", "scaffold", "boilerplate",
                "add ", "create ", "change ", "update ", "edit ", "write ", "modify ",
                "delete ", "remove ", "replace ", "insert ", "apply "
            ];
            if (editHints.Any(h => p.Contains(h, StringComparison.Ordinal)))
                return true;

            // Explicit file targets often imply edits.
            if (System.Text.RegularExpressions.Regex.IsMatch(p, @"\b[\w./\\-]+\.(cs|ts|tsx|js|jsx|py|java|go|rs|cpp|h|html|css|json|md|yml|yaml)\b"))
            {
                string[] softEdit = ["in ", "to ", "the ", "our ", "my "];
                // "explain Main.cs" is not an edit; "update Main.cs" already matched above.
                string[] explore = ["what", "how", "why", "explain", "summarize", "list", "show", "describe", "review", "where"];
                if (explore.Any(e => p.Contains(e, StringComparison.Ordinal)))
                    return false;
                return softEdit.Any(s => p.Contains(s, StringComparison.Ordinal));
            }

            return false;
        }

        private static bool ContainsPatchEnvelope(string text)
            => !string.IsNullOrWhiteSpace(text)
               && text.Contains("[[AXIOM_CODEBASE_PATCH]]", StringComparison.OrdinalIgnoreCase);

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
