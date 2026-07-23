using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Workspace;
// ProjectMemory lives in Agent namespace.

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

        // Used throughout this class to scale round budgets, tool exposure, prompt size, and
        // context budgets down for a small self-hosted model instead of assuming a large cloud
        // window everywhere (see Axiom-CLI plan "Fix Axiom-CLI's agent/council behavior on small
        // local models (kestral)").
        private bool IsCustomEndpointModel =>
            string.Equals(_modelId, OpenRouterChatService.CustomEndpointModelId, StringComparison.OrdinalIgnoreCase);

        // WorkspaceContextBudgetChars (60,000) assumes a 131k+-token cloud window. Kestral's real
        // window can be a small fraction of that -- scale the budget to the real window instead,
        // using the same ~35%-of-window-at-3.5-chars/token ratio already validated for the
        // sibling desktop app's Hybrid Local document budget.
        private int ResolveWorkspaceContextBudgetChars()
        {
            if (!IsCustomEndpointModel)
                return WorkspaceContextBudgetChars;

            int contextWindow = _chat?.GetApproximateContextWindowTokens(_modelId) ?? OpenRouterChatService.CustomEndpointContextWindowTokens;
            const double workspaceShareOfWindow = 0.35;
            const double approxCharsPerToken = 3.5;
            return (int)(contextWindow * workspaceShareOfWindow * approxCharsPerToken);
        }

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
                    request.Workspace!, request.UserPrompt, ResolveWorkspaceContextBudgetChars());
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

            CouncilTaskKind taskKind = CouncilRolePrompts.DetectTaskKind(request.UserPrompt);
            TaskSpecialty specialty = IntelligenceHelpers.DetectSpecialty(request.UserPrompt);
            GoalContract goal = GoalContract.FromPrompt(request.UserPrompt);
            string memoryBlock = string.Empty;
            string rootPath = request.Workspace?.RootPath ?? "";
            if (workspaceConnected && !string.IsNullOrWhiteSpace(rootPath))
                memoryBlock = ProjectMemory.BuildContextBlock(rootPath);
            if (!string.IsNullOrWhiteSpace(memoryBlock))
            {
                workspaceContext = string.IsNullOrWhiteSpace(workspaceContext)
                    ? memoryBlock
                    : memoryBlock + "\n\n" + workspaceContext;
                Report(progress, CouncilEventKind.Status, "Project memory loaded (AXIOM.md / AGENTS.md).");
            }
            string goalBlock = goal.ToPromptBlock();
            if (!string.IsNullOrWhiteSpace(goalBlock))
            {
                workspaceContext = string.IsNullOrWhiteSpace(workspaceContext)
                    ? goalBlock
                    : goalBlock + "\n\n" + workspaceContext;
            }

            // Repo map + lexical retrieval for smarter planning
            if (workspaceConnected && !string.IsNullOrWhiteSpace(rootPath))
            {
                try
                {
                    string map = RepoMapService.Build(rootPath);
                    string ret = RepoRetrievalService.Retrieve(rootPath, request.UserPrompt);
                    string intel = string.Join("\n\n", new[] { map, ret }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (!string.IsNullOrWhiteSpace(intel))
                    {
                        workspaceContext = string.IsNullOrWhiteSpace(workspaceContext)
                            ? intel
                            : intel + "\n\n" + workspaceContext;
                        Report(progress, CouncilEventKind.Status, "Repo map / retrieval attached.");
                    }
                }
                catch { /* optional */ }
            }

            string specialtyBlock = IntelligenceHelpers.SpecialtyPromptBlock(specialty);
            if (!string.IsNullOrWhiteSpace(specialtyBlock))
            {
                workspaceContext = string.IsNullOrWhiteSpace(workspaceContext)
                    ? specialtyBlock
                    : specialtyBlock + "\n\n" + workspaceContext;
            }

            string? regression = _agentTools?.Workflow.RegressionGuardBlock();
            if (!string.IsNullOrWhiteSpace(regression))
            {
                workspaceContext = string.IsNullOrWhiteSpace(workspaceContext)
                    ? regression
                    : regression + "\n\n" + workspaceContext;
            }

            string acceptance = AcceptanceCriteria.Load(rootPath);
            if (!string.IsNullOrWhiteSpace(acceptance))
            {
                workspaceContext = string.IsNullOrWhiteSpace(workspaceContext)
                    ? acceptance
                    : acceptance + "\n\n" + workspaceContext;
                Report(progress, CouncilEventKind.Status, "Acceptance criteria loaded (.axiom/acceptance.md).");
            }

            if (agentic)
                Report(progress, CouncilEventKind.Status, "Council · Builder has agentic tools (files/shell/git).");
            Report(progress, CouncilEventKind.Status,
                $"Council · severity {CriticSeverity.Describe(tools.SeverityPolicy)} · " +
                $"task {taskKind} · specialty {specialty}.");

            _agentTools?.BeginUndoTurn("council");
            bool cancelled = false;
            string? exploreSummary = null;

            string architectPlan = string.Empty;
            string builderOutput = string.Empty;
            try
            {
            // ── Parallel explore lane (while Architect plans) ─────────────────
            Task<(string Summary, int Tools)>? exploreTask = null;
            if (tools.ParallelExplore && agentic && workspaceConnected && _chat != null && _agentTools != null
                && (taskKind == CouncilTaskKind.Coding || looksLikeEdit || specialty == TaskSpecialty.Debug))
            {
                Report(progress, CouncilEventKind.Status, "Explore lane starting in parallel with Architect…");
                exploreTask = RunExploreLaneAsync(request.UserPrompt, progress, cancellationToken);
            }

            // ── Architect: plan (desktop Workplace role contract) ─────────────
            Report(progress, CouncilEventKind.Status, "Architect is planning...");
            string architectSystem = FoundationSystemPrompt.Apply(
                CouncilRolePrompts.Architect(taskKind, workspaceConnected, agentic, IsCustomEndpointModel));
            string architectInput = BuildContextualInput(request.UserPrompt, workspaceContext);
            architectPlan = CouncilRolePrompts.StripRoleMarkers(
                await CallRoleAsync(architectSystem, architectInput, progress, cancellationToken, streamTokens: true));

            // Architect plan validation — one repair pass if empty/unusable (coding/edit only)
            string planErr = IntelligenceHelpers.ArchitectValidationError(
                architectPlan, taskKind, codingPathsRequired: looksLikeEdit || taskKind == CouncilTaskKind.Coding);
            if (!string.IsNullOrEmpty(planErr)
                && (taskKind == CouncilTaskKind.Coding || looksLikeEdit))
            {
                Report(progress, CouncilEventKind.Warning, "Architect plan weak: " + planErr + " — asking for a clearer plan.");
                string repairInput = architectInput
                    + "\n\n[PLAN VALIDATION FAILED]\n" + planErr
                    + "\nRewrite a numbered plan only. For coding tasks name concrete file paths. No vague verbs.";
                architectPlan = CouncilRolePrompts.StripRoleMarkers(
                    await CallRoleAsync(architectSystem, repairInput, progress, cancellationToken, streamTokens: true));
            }

            Report(progress, CouncilEventKind.ArchitectOutput, architectPlan);

            // Seed plan board from Architect numbered steps (user can /plan; Builder can plan_board).
            if (_agentTools != null && !string.IsNullOrWhiteSpace(architectPlan))
            {
                _agentTools.Workflow.Plan.SetFromArchitectPlan(architectPlan);
                if (_agentTools.Workflow.Plan.HasSteps)
                    Report(progress, CouncilEventKind.Status, "Plan board loaded from Architect steps.");
            }

            // Sticky task + plan board + git branch context for Builder
            string workflowExtra = await BuildWorkflowContextAsync(request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(workflowExtra))
            {
                workspaceContext = string.IsNullOrWhiteSpace(workspaceContext)
                    ? workflowExtra
                    : workflowExtra + "\n\n" + workspaceContext;
            }

            // Merge parallel explore results into Builder context
            if (exploreTask != null)
            {
                try
                {
                    (string exploreText, int exploreTools) = await exploreTask;
                    totalToolCalls += exploreTools;
                    if (!string.IsNullOrWhiteSpace(exploreText))
                    {
                        exploreSummary = exploreText.Length > 4000 ? exploreText[..4000] + "…" : exploreText;
                        Report(progress, CouncilEventKind.ExploreOutput, exploreSummary);
                        workspaceContext += "\n\n[[EXPLORE LANE FINDINGS]]\n" + exploreSummary + "\n[[END EXPLORE LANE]]";
                        Report(progress, CouncilEventKind.Status, "Explore lane merged into Builder context.");
                    }
                }
                catch (Exception ex)
                {
                    Report(progress, CouncilEventKind.Warning, "Explore lane failed: " + ex.Message);
                }
            }

            // ── Builder: implement (agentic tool loop when available) ──────────
            // Computed once here so the system prompt's tool-name prose matches exactly what
            // ToolCallingLoop.RunAsync will independently (but deterministically) resolve for the
            // same inputs -- avoids telling the model about tools it can't actually call this turn.
            var builderToolsForPrompt = agentic
                ? _agentTools?.GetToolDefinitions(AgentToolExecutor.ToolScope.Full, request.UserPrompt, IsCustomEndpointModel)
                : null;
            string builderSystem = FoundationSystemPrompt.Apply(
                CouncilRolePrompts.Builder(taskKind, workspaceConnected, expectPatch, agentic, looksLikeEdit, builderToolsForPrompt, IsCustomEndpointModel));
            string builderInput = BuildBuilderInput(request.UserPrompt, architectPlan, workspaceContext);

            Report(progress, CouncilEventKind.Status,
                agentic ? "Builder is implementing with tools..." : "Builder is implementing...");
            _agentTools?.ClearWrittenPaths();
            (builderOutput, int builderTools) = await CallBuilderAsync(
                builderSystem, builderInput, agentic, progress, cancellationToken, gatingMessage: request.UserPrompt);
            builderOutput = CouncilRolePrompts.StripRoleMarkers(builderOutput);
            totalToolCalls += builderTools;
            CollectWrittenFiles(changedFiles);

            // A small model can narrate "I've made the change" without ever emitting a real tool
            // call or a patch envelope -- give it exactly one explicit nudge before accepting
            // nothing happened as success (expectPatch is bypassed entirely for agentic Builders,
            // so without this nothing else would catch this).
            bool builderWroteNothingForEdit = agentic && IsCustomEndpointModel && looksLikeEdit
                && _agentTools != null && _agentTools.WrittenPaths.Count == 0
                && !ContainsPatchEnvelope(builderOutput);
            if (builderWroteNothingForEdit)
            {
                Report(progress, CouncilEventKind.Warning,
                    "Builder produced no tool calls or patch for an edit request — retrying with an explicit tool-use instruction.");
                string nudgedInput = builderInput +
                    "\n\n[NO TOOL CALLS DETECTED] You MUST call write_file or str_replace now to make the " +
                    "requested change on disk — describing the change is not sufficient. Call a tool before responding.";
                (builderOutput, int retryTools) = await CallBuilderAsync(
                    builderSystem, nudgedInput, agentic, progress, cancellationToken, gatingMessage: request.UserPrompt);
                builderOutput = CouncilRolePrompts.StripRoleMarkers(builderOutput);
                totalToolCalls += retryTools;
                CollectWrittenFiles(changedFiles);
            }

            // Builder self-check: auto diagnostics after writes before Critic
            if (agentic && _agentTools != null && _agentTools.Workflow.AutoDiagnosticsAfterWrite
                && _agentTools.WrittenPaths.Count > 0
                && !string.IsNullOrWhiteSpace(rootPath))
            {
                try
                {
                    Report(progress, CouncilEventKind.Status, "Builder self-check · diagnostics…");
                    string diag = await DiagnosticsService.RunAsync(rootPath, cancellationToken);
                    totalToolCalls++;
                    if (!string.IsNullOrWhiteSpace(diag))
                    {
                        builderOutput = builderOutput.TrimEnd()
                            + "\n\n[[BUILDER SELF-CHECK DIAGNOSTICS]]\n"
                            + (diag.Length > 6000 ? diag[..6000] + "\n..." : diag)
                            + "\n[[END BUILDER SELF-CHECK]]";
                        NoteSessionTestOutcomes(diag);
                    }
                }
                catch (Exception ex)
                {
                    Report(progress, CouncilEventKind.Warning, "Self-check diagnostics skipped: " + ex.Message);
                }
            }

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
            // Dozens of round-trips (Builder retries x ToolCallingLoop rounds each) reads as
            // "endless looping" on a small local model even though it's bounded -- cap the retry
            // budget tighter for the custom endpoint. eidos/hepha keep the original budget.
            int maxBuilderRetryAttempts = IsCustomEndpointModel ? 1 : MaxBuilderRetryAttempts;
            CriticReport report;
            while (true)
            {
                // Deterministic rails before every Critic call (desktop Stage 2.5 / sandbox).
                CriticEvidence evidence = await GatherCriticEvidenceAsync(
                    builderOutput, expectPatch || patch != null, tools, progress, cancellationToken);

                Report(progress, CouncilEventKind.Status, "Critic is reviewing...");
                string criticInput = BuildCriticInput(request.UserPrompt, patch != null, builderOutput, evidence);
                if (!string.IsNullOrWhiteSpace(acceptance))
                    criticInput = acceptance + "\n\n" + criticInput;
                bool criticInspect = agentic && workspaceConnected;
                string criticSystem = FoundationSystemPrompt.Apply(
                    CouncilRolePrompts.Critic(taskKind, workspaceConnected, criticInspect, IsCustomEndpointModel));
                (string criticOutput, int criticTools) = await CallCriticAsync(
                    criticSystem, criticInput, criticInspect, progress, cancellationToken);
                criticOutput = CouncilRolePrompts.StripRoleMarkers(criticOutput);
                totalToolCalls += criticTools;
                Report(progress, CouncilEventKind.CriticOutput, criticOutput);
                report = CriticContractParser.Parse(criticOutput);

                // Deterministic findings force an issues status even if the LLM clean-passes.
                report = MergeDeterministicFindings(report, evidence);

                // Evidence quality rails: annotate missing evidence; only reject vague clean-pass
                // when Builder actually changed files (real agentic run), not scripted unit tests.
                bool hadRails = evidence.Findings.Count > 0 || !string.IsNullOrWhiteSpace(evidence.SandboxLogs);
                report = IntelligenceHelpers.EnforceCriticEvidence(
                    report, codingTask: taskKind == CouncilTaskKind.Coding || looksLikeEdit, hadRails);
                bool builderChangedDisk = _agentTools != null && _agentTools.WrittenPaths.Count > 0;
                if (builderChangedDisk
                    && IntelligenceHelpers.CriticOutputLacksEvidence(criticOutput, report)
                    && (taskKind == CouncilTaskKind.Coding || looksLikeEdit)
                    && retries == 0)
                {
                    report.Issues ??= new List<CriticIssue>();
                    report.Issues.Add(new CriticIssue
                    {
                        Severity = "medium",
                        Summary = "Critic clean-pass lacked file:line or test evidence",
                        Evidence = "No path:line citation or diagnostics reference in review",
                        SuggestedFix = "Re-read Builder changes and cite concrete evidence, or re-run tests."
                    });
                    report.Status = "issues";
                    report.HasIssues = true;
                    report.FindingsCount = report.Issues.Count;
                    Report(progress, CouncilEventKind.Warning, "Critic evidence rule: vague clean-pass rejected.");
                }

                // Still nothing on disk after the Phase-4 nudge above -- do not let this pass as a
                // clean review just because the Critic (also on a small model) didn't catch it.
                if (builderWroteNothingForEdit && !builderChangedDisk && retries == 0)
                {
                    report.Issues ??= new List<CriticIssue>();
                    report.Issues.Add(new CriticIssue
                    {
                        Severity = "high",
                        Summary = "Builder did not write any files or produce a patch for an edit request",
                        Evidence = "No tool calls or [[AXIOM_CODEBASE_PATCH]] envelope were produced, even after an explicit retry",
                        SuggestedFix = "Re-run with a more explicit instruction to call write_file/str_replace, or break the task into a smaller step."
                    });
                    report.Status = "issues";
                    report.HasIssues = true;
                    report.FindingsCount = report.Issues.Count;
                    Report(progress, CouncilEventKind.Warning, "Builder wrote nothing for an edit request — flagging instead of accepting as done.");
                }

                // Severity policy: only blocking issues force revision
                var blocking = CriticSeverity.FilterBlocking(report.Issues, tools.SeverityPolicy);
                int issueCount = blocking.Count;
                int totalIssues = report.Issues?.Count ?? 0;
                if (totalIssues > issueCount)
                {
                    Report(progress, CouncilEventKind.Status,
                        $"Critic: {totalIssues} finding(s), {issueCount} block under severity={CriticSeverity.Describe(tools.SeverityPolicy)}.");
                }

                if (issueCount == 0)
                {
                    Report(progress, CouncilEventKind.Status,
                        totalIssues == 0
                            ? "Critic found no issues."
                            : "No blocking issues at current severity — continuing.");
                    break;
                }

                // User-in-the-loop: let host pick which blocking issues to fix
                if (tools.UserInLoopCritic && request.OnPickCriticIssues != null)
                {
                    Report(progress, CouncilEventKind.Status,
                        "Waiting for you to pick Critic findings to fix (all / none / 1,3)…");
                    IReadOnlyList<int>? picked = await request.OnPickCriticIssues(blocking, cancellationToken);
                    if (picked != null && picked.Count == 0)
                    {
                        Report(progress, CouncilEventKind.Status, "User accepted remaining Critic findings — stopping revisions.");
                        break;
                    }
                    if (picked is { Count: > 0 })
                    {
                        var selected = new List<CriticIssue>();
                        foreach (int idx in picked)
                        {
                            if (idx >= 1 && idx <= blocking.Count)
                                selected.Add(blocking[idx - 1]);
                        }
                        if (selected.Count > 0)
                            blocking = selected;
                    }
                }

                if (retries >= maxBuilderRetryAttempts)
                {
                    Report(progress, CouncilEventKind.Warning,
                        $"Builder retry limit reached ({maxBuilderRetryAttempts}); keeping current output. " +
                        $"{issueCount} blocking Critic finding(s) remain.");
                    break;
                }

                retries++;
                bool fullRevision = issueCount > TargetedPatchIssueCeiling
                    || evidence.SandboxFailed;
                Report(progress, CouncilEventKind.Status,
                    fullRevision
                        ? $"Critic found {issueCount} blocking issues — running full revision ({retries}/{maxBuilderRetryAttempts})..."
                        : $"Critic found {issueCount} blocking issue(s) — running targeted patch ({retries}/{maxBuilderRetryAttempts})...");

                // Revision prompt focuses on blocking (and user-selected) issues
                string focusedCritic = FormatIssuesForRevision(blocking, criticOutput);
                string revisionSystem = fullRevision
                    ? builderSystem
                    : builderSystem + "\n" + TargetedPatchModeNote(expectPatch || patch != null, agentic);
                string revisionInput = BuildRevisionInput(
                    request.UserPrompt, architectPlan, workspaceContext, builderOutput, focusedCritic, fullRevision, evidence);

                (builderOutput, int revisionTools) = await CallBuilderAsync(
                    revisionSystem, revisionInput, agentic, progress, cancellationToken, gatingMessage: request.UserPrompt);
                builderOutput = CouncilRolePrompts.StripRoleMarkers(builderOutput);
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

            // ── Post-merge Critic: re-check after apply / disk writes ──────────
            bool wroteSomething = changedFiles.Count > 0
                || (_agentTools?.WrittenPaths.Count ?? 0) > 0;
            if (tools.PostMergeCritic && wroteSomething && workspaceConnected)
            {
                try
                {
                    Report(progress, CouncilEventKind.Status, "Post-merge Critic · re-checking changes…");
                    string postBody = builderOutput;
                    if (!string.IsNullOrWhiteSpace(rootPath))
                    {
                        try
                        {
                            string diag = await DiagnosticsService.RunAsync(rootPath, cancellationToken);
                            totalToolCalls++;
                            postBody += "\n\n[[POST-MERGE DIAGNOSTICS]]\n"
                                + (diag.Length > 4000 ? diag[..4000] + "…" : diag);
                            NoteSessionTestOutcomes(diag);
                        }
                        catch { /* optional */ }
                    }

                    CriticEvidence postEv = await GatherCriticEvidenceAsync(
                        postBody, true, tools, progress, cancellationToken);
                    string postInput = BuildCriticInput(request.UserPrompt, true, postBody, postEv);
                    if (!string.IsNullOrWhiteSpace(acceptance))
                        postInput = acceptance + "\n\n" + postInput;
                    postInput += "\n\n[POST-MERGE] Review the applied changes and diagnostics. " +
                                 "Flag regressions only.";
                    string postSystem = FoundationSystemPrompt.Apply(
                        CouncilRolePrompts.Critic(taskKind, true, agentic, IsCustomEndpointModel));
                    (string postCritic, int postTools) = await CallCriticAsync(
                        postSystem, postInput, agentic, progress, cancellationToken);
                    totalToolCalls += postTools;
                    Report(progress, CouncilEventKind.CriticOutput, postCritic);
                    var postReport = MergeDeterministicFindings(CriticContractParser.Parse(postCritic), postEv);
                    var postBlocking = CriticSeverity.FilterBlocking(postReport.Issues, tools.SeverityPolicy);
                    if (postBlocking.Count > 0)
                    {
                        report = postReport;
                        Report(progress, CouncilEventKind.Warning,
                            $"Post-merge Critic: {postBlocking.Count} blocking finding(s) after apply.");
                    }
                    else
                    {
                        Report(progress, CouncilEventKind.Status, "Post-merge Critic: no blocking findings.");
                    }
                }
                catch (Exception ex)
                {
                    Report(progress, CouncilEventKind.Warning, "Post-merge Critic skipped: " + ex.Message);
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
                applySummary,
                cancelled,
                exploreSummary);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                Report(progress, CouncilEventKind.Warning, "Council run stopped by user.");
                return new CouncilResult(
                    false,
                    builderOutput ?? string.Empty,
                    null,
                    new CriticReport { Status = "issues" },
                    totalToolCalls,
                    changedFiles.Count > 0 ? changedFiles : null,
                    applySummary,
                    Cancelled: true,
                    exploreSummary);
            }
            finally
            {
                _agentTools?.CommitUndoTurn();
            }
        }

        private async Task<(string Summary, int Tools)> RunExploreLaneAsync(
            string userPrompt,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            if (_chat == null || _agentTools == null)
                return ("", 0);

            string system = FoundationSystemPrompt.Apply(
                "You are a read-only Explore lane running in parallel with the Architect. " +
                "Map the relevant files, symbols, and risks for the user request. " +
                "Use list_dir, read_file, search_files, find_symbol only. Do not modify files. " +
                "Return a compact bullet list of paths and findings.");
            string input = userPrompt + "\n\n" + (_agentTools != null
                ? "" // tools see workspace via executor
                : "");
            // Attach workspace block via primary path memory if available
            try
            {
                // Subagent-style inspect loop
                var loop = new ToolCallingLoop(_chat, _agentTools, _modelId, maxRounds: 6);
                ToolCallingResult result = await loop.RunAsync(
                    system,
                    userPrompt + "\n\nExplore the connected workspace for this request. Summarize relevant paths only.",
                    status => Report(progress, CouncilEventKind.Status, "Explore · " + status),
                    cancellationToken,
                    AgentToolExecutor.ToolScope.Inspect,
                    onToolEvent: ev => ReportToolEvent(progress, "Explore", ev),
                    onToken: null);
                return (result.FinalText ?? "", result.ToolCallCount);
            }
            catch (Exception ex)
            {
                return ("Explore failed: " + ex.Message, 0);
            }
        }

        private static string FormatIssuesForRevision(IReadOnlyList<CriticIssue> issues, string fallbackCriticOutput)
        {
            if (issues == null || issues.Count == 0)
                return fallbackCriticOutput;
            var sb = new StringBuilder();
            sb.AppendLine("{\"status\":\"issues\",\"issues\":[");
            for (int i = 0; i < issues.Count; i++)
            {
                var iss = issues[i];
                if (i > 0) sb.Append(',');
                sb.Append('{')
                    .Append("\"severity\":\"").Append(EscapeJson(iss.Severity)).Append("\",")
                    .Append("\"summary\":\"").Append(EscapeJson(iss.Summary)).Append("\",")
                    .Append("\"evidence\":\"").Append(EscapeJson(iss.Evidence)).Append("\",")
                    .Append("\"suggestedFix\":\"").Append(EscapeJson(iss.SuggestedFix)).Append("\"")
                    .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string EscapeJson(string? s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

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
                (builderOutput, _) = await CallBuilderAsync(builderSystem, retryInput, agentic, progress, cancellationToken, gatingMessage: request.UserPrompt);
                Report(progress, CouncilEventKind.BuilderOutput, builderOutput);
            }

            return null;
        }

        private async Task<(string Text, int ToolCalls)> CallBuilderAsync(
            string systemPrompt,
            string userInput,
            bool agentic,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken,
            string? gatingMessage = null)
        {
            if (!agentic || _chat == null || _agentTools == null)
            {
                string text = await CallRoleAsync(systemPrompt, userInput, progress, cancellationToken, streamTokens: true);
                return (text, 0);
            }

            // Dozens of rounds on a small local model reads as "endless looping" -- cap tighter
            // for the custom endpoint. eidos/hepha keep the original 12-round budget.
            var loop = new ToolCallingLoop(_chat, _agentTools, _modelId, maxRounds: IsCustomEndpointModel ? 6 : ToolCallingLoop.DefaultMaxRounds);
            ToolCallingResult result = await loop.RunAsync(
                systemPrompt,
                userInput,
                status => Report(progress, CouncilEventKind.Status, "Builder · " + status),
                cancellationToken,
                AgentToolExecutor.ToolScope.Full,
                onToolEvent: ev => ReportToolEvent(progress, "Builder", ev),
                onToken: tok => progress?.Report(new CouncilEvent(CouncilEventKind.Token, tok)),
                gateForCustomEndpoint: IsCustomEndpointModel,
                gatingMessage: gatingMessage);

            if (result.ToolCallCount > 0)
            {
                Report(progress, CouncilEventKind.Status,
                    $"Builder · {result.ToolCallCount} tool call(s) completed.");
            }

            return (result.FinalText, result.ToolCallCount);
        }

        private async Task<(string Text, int ToolCalls)> CallCriticAsync(
            string systemPrompt,
            string userInput,
            bool inspectTools,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            if (!inspectTools || _chat == null || _agentTools == null)
            {
                string text = await CallRoleAsync(systemPrompt, userInput, progress, cancellationToken, streamTokens: true);
                return (text, 0);
            }

            var loop = new ToolCallingLoop(_chat, _agentTools, _modelId);
            ToolCallingResult result = await loop.RunAsync(
                systemPrompt,
                userInput,
                status => Report(progress, CouncilEventKind.Status, "Critic · " + status),
                cancellationToken,
                AgentToolExecutor.ToolScope.Inspect,
                onToolEvent: ev => ReportToolEvent(progress, "Critic", ev),
                onToken: tok => progress?.Report(new CouncilEvent(CouncilEventKind.Token, tok)));

            if (result.ToolCallCount > 0)
            {
                Report(progress, CouncilEventKind.Status,
                    $"Critic · {result.ToolCallCount} inspect tool call(s).");
            }

            return (result.FinalText, result.ToolCallCount);
        }

        private void NoteSessionTestOutcomes(string output)
        {
            if (_agentTools == null || string.IsNullOrWhiteSpace(output))
                return;
            bool failed = output.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                || (output.Contains("exit_code: ", StringComparison.Ordinal)
                    && !output.Contains("exit_code: 0", StringComparison.Ordinal));
            if (failed)
                _agentTools.Workflow.NoteFailedTest("council-diagnostics");
            else if (output.Contains("exit_code: 0", StringComparison.Ordinal)
                     || output.Contains("Passed!", StringComparison.OrdinalIgnoreCase))
                _agentTools.Workflow.NoteTestsPassedClear("council-diagnostics");
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

        private static void ReportToolEvent(IProgress<CouncilEvent>? progress, string role, ToolEvent ev)
        {
            string mark = ev.Phase switch
            {
                ToolEventPhase.Started => "●",
                ToolEventPhase.Finished => "✓",
                ToolEventPhase.Denied => "✗",
                ToolEventPhase.Planned => "◇",
                _ => "·"
            };
            string line = $"{mark} {role} · {ev.ToolName}";
            if (!string.IsNullOrWhiteSpace(ev.Detail))
                line += "  " + (ev.Detail.Length > 80 ? ev.Detail[..77] + "…" : ev.Detail);
            if (ev.Phase != ToolEventPhase.Started && !string.IsNullOrWhiteSpace(ev.ResultPreview))
                line += " → " + (ev.ResultPreview!.Length > 60 ? ev.ResultPreview[..57] + "…" : ev.ResultPreview);
            progress?.Report(new CouncilEvent(CouncilEventKind.Tool, line));
        }

        private async Task<string> CallRoleAsync(
            string systemPrompt,
            string userInput,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken,
            bool streamTokens = false)
        {
            // Prefer direct OpenRouter streaming when available so Architect/Critic tokens appear live.
            if (streamTokens && _chat != null)
            {
                var messages = new List<OpenRouterMessage> { new("user", userInput, PreserveFullText: true) };
                var collected = new StringBuilder();
                OpenRouterChatResponse response = await _chat.SendConversationStreamAsync(
                    messages,
                    systemPrompt,
                    thinkingEnabled: false,
                    modelId: _modelId,
                    tools: null,
                    onToken: tok =>
                    {
                        collected.Append(tok);
                        progress?.Report(new CouncilEvent(CouncilEventKind.Token, tok));
                    },
                    cancellationToken);
                return !string.IsNullOrEmpty(response.Text) ? response.Text : collected.ToString();
            }

            var request = new ChatPipelineRequest(systemPrompt, userInput);
            ChatPipelineResult result = await _pipeline.ExecuteAsync(
                request,
                onToken: streamTokens
                    ? tok => progress?.Report(new CouncilEvent(CouncilEventKind.Token, tok))
                    : null,
                cancellationToken);
            return result.ResponseText;
        }

        private static string TargetedPatchModeNote(bool patchMode, bool agentic) => agentic
            ? "[TARGETED FIX MODE] Fix ONLY the specific issues listed below using tools " +
              "(write_file / run_shell). Then summarize. Do not change unrelated files."
            : patchMode
                ? "[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. Output a complete, " +
                  "valid [[AXIOM_CODEBASE_PATCH]] envelope — do not output a diff or partial snippet, " +
                  "and do not change anything not mentioned in the findings."
                : "[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. Output the complete " +
                  "corrected response and preserve unaffected content. Do not output a diff.";

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

        private async Task<string> BuildWorkflowContextAsync(CouncilRequest request, CancellationToken token)
        {
            if (_agentTools == null)
                return string.Empty;

            var sb = new StringBuilder();
            string? sticky = _agentTools.Workflow.ConsumeStickyPrefix();
            if (!string.IsNullOrWhiteSpace(sticky))
                sb.Append(sticky);

            string plan = _agentTools.Workflow.Plan.ToPromptBlock();
            if (!string.IsNullOrWhiteSpace(plan))
                sb.AppendLine(plan);

            string? root = request.Workspace?.RootPath;
            if (!string.IsNullOrWhiteSpace(root))
            {
                try
                {
                    GitBranchSnapshot snap = await GitBranchContext.CaptureAsync(root, token);
                    string git = GitBranchContext.ToPromptBlock(snap);
                    if (!string.IsNullOrWhiteSpace(git))
                        sb.AppendLine(git);
                }
                catch { /* optional */ }
            }

            return sb.ToString().Trim();
        }

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
