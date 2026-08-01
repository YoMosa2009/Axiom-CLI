using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Axiom.Core.Council;

namespace Axiom.Core.Agent
{
    public sealed record AgentTurnResult(
        string ResponseText,
        TimeSpan Elapsed,
        int ToolCallCount,
        int EstimatedPromptTokens,
        int ContextWindowTokens,
        bool Failed = false,
        bool Cancelled = false,
        string? BudgetWarning = null);

    // Multi-step tool-using chat turn: model may call shell/file/git tools repeatedly
    // (bounded) before producing a final user-facing answer.
    public sealed class AgentLoop
    {
        private readonly OpenRouterChatService _chat;
        private readonly AgentToolExecutor _tools;
        private readonly WorkspaceSession _workspace;
        private readonly string _modelId;
        private readonly KestralMemoryStore? _kestralMemory;
        private readonly EffortLevel _effort;

        public AgentLoop(
            OpenRouterChatService chat,
            AgentToolExecutor tools,
            WorkspaceSession workspace,
            string modelId,
            KestralMemoryStore? kestralMemory = null,
            EffortLevel effort = EffortLevel.Medium)
        {
            _chat = chat;
            _tools = tools;
            _workspace = workspace;
            _modelId = modelId;
            _kestralMemory = kestralMemory;
            _effort = effort;
        }

        public async Task<AgentTurnResult> RunAsync(
            string userMessage,
            List<OpenRouterMessage> history,
            Action<string>? onToken,
            Action<string>? onStatus,
            CancellationToken cancellationToken,
            Action<ToolEvent>? onToolEvent = null)
        {
            var sw = Stopwatch.StartNew();
            bool failed = false;
            bool cancelled = false;

            // Kestral (the custom endpoint) has a real ~8k-token window vs. a 131k+ cloud window --
            // repo map / retrieval / few-shot are the most expendable per-turn additions (highest
            // token cost relative to value on a "hello"-shaped message), so skip them unless the
            // message actually looks like it needs codebase context.
            bool isCustomEndpoint = string.Equals(_modelId, OpenRouterChatService.CustomEndpointModelId, StringComparison.OrdinalIgnoreCase);
            bool looksLikeEdit = CouncilOrchestrator.LooksLikeCodeEditRequest(userMessage);
            bool includeExpensiveContext = !isCustomEndpoint || looksLikeEdit;

            TaskSpecialty specialty = IntelligenceHelpers.DetectSpecialty(userMessage);
            GoalContract goal = GoalContract.FromPrompt(userMessage);
            // GoalContract.RequiresWrittenArtifacts only fires on explicit edit-shaped wording
            // ("fix", "add", "update", ...) in THIS message alone. That misses a real, live-reported
            // pattern: a short follow-up in the middle of an ongoing coding session ("that's still
            // not right", "try again", "no, that's wrong") that never repeats an edit keyword but is
            // unambiguously asking for more work on the same task. Without requiresWrittenArtifacts,
            // the "nothing was written" retry-and-failure safety net a few lines below never engages
            // for that turn at all, so a model that responds with just narration instead of a real
            // fix sails straight through undetected -- which is exactly the "I keep telling it and it
            // still doesn't fix it" complaint this was built to catch. Broadened to also cover any
            // non-trivial follow-up mid-session, while still excluding small talk and genuine
            // questions so a real "why did you do X?" doesn't get force-nudged into an unwanted edit.
            bool looksLikeOngoingTaskFollowUp = isCustomEndpoint
                && history.Count > 0
                && _workspace.Roots.Count > 0
                && !ToolGatingHeuristics.LooksLikeSmallTalk(userMessage)
                && !LooksLikePureQuestion(userMessage);
            bool requiresWrittenArtifacts = goal.RequiresWrittenArtifacts || looksLikeOngoingTaskFollowUp;
            string goalBlock = goal.ToPromptBlock();
            // Same tool list ToolCallingLoop.RunAsync will independently (but deterministically)
            // resolve below -- computed here too so the system prompt's tool enumeration matches
            // what's actually offered instead of a static ~19-tool catalog. A genuine simplification
            // for every model: the filter is a no-op unless isCustomEndpoint is true.
            var toolsForPrompt = _tools.GetToolDefinitions(AgentToolExecutor.ToolScope.Full, userMessage, isCustomEndpoint);
            bool planBoardAvailable = toolsForPrompt.Any(tool => tool.Name == "plan_board");
            string system = FoundationSystemPrompt.Apply(BuildAgentSystemPrompt(_tools.ApprovalMode, isCustomEndpoint));

            string workspaceBlock = _workspace.BuildContextBlock(isCustomEndpoint ? 40 : 120);
            string memory = ProjectMemory.BuildContextBlock(_workspace.PrimaryRoot);
            string sticky = _tools.Workflow.ConsumeStickyPrefix() ?? string.Empty;
            string planBlock = _tools.Workflow.Plan.ToPromptBlock();
            string repoMap = string.Empty;
            string retrieval = string.Empty;
            string kestralMem = string.Empty;
            string gitBlock = string.Empty;
            string root = _workspace.PrimaryRoot;

            try
            {
                GitBranchSnapshot gitSnap = await GitBranchContext.CaptureAsync(root, cancellationToken);
                gitBlock = GitBranchContext.ToPromptBlock(gitSnap);
            }
            catch { /* optional */ }

            try
            {
                if (!string.IsNullOrWhiteSpace(root) && includeExpensiveContext)
                {
                    onStatus?.Invoke("Mapping repo");
                    repoMap = RepoMapService.Build(root);
                    retrieval = RepoRetrievalService.Retrieve(root, userMessage);
                }
            }
            catch { /* optional */ }

            // Kestral-only persistent memory: retrieval is a cheap indexed SQL query so it always
            // runs when a workspace is attached; ingestion follows the same includeExpensiveContext
            // gating as RepoMapService/RepoRetrievalService above since it's comparable in cost.
            if (isCustomEndpoint && _kestralMemory != null && !string.IsNullOrWhiteSpace(root))
            {
                try
                {
                    if (includeExpensiveContext)
                        _kestralMemory.IngestWorkspace(root, cancellationToken);
                    kestralMem = _kestralMemory.Retrieve(root, userMessage);
                }
                catch { /* optional */ }
            }

            string fewShot = includeExpensiveContext ? IntelligenceHelpers.FewShotFromHistory(history, userMessage) : string.Empty;
            string? regression = _tools.Workflow.RegressionGuardBlock();
            string specialtyBlock = IntelligenceHelpers.SpecialtyPromptBlock(specialty);

            // Compact history for long sessions. Custom-endpoint thresholds scale to Kestral's
            // real (now much larger) window instead of the fixed ceiling tuned for cloud models --
            // see ConversationCompactor.Compact -- and keep more recent messages verbatim since the
            // bigger window affords it, preserving more usable detail before summarization kicks
            // in. How many is now effort-tiered (EffortPolicy.KeepRecentMessages): Low compacts
            // sooner to keep turns fast, Max keeps the most detail in view at the cost of a larger
            // prompt.
            int contextWindow = _chat.GetApproximateContextWindowTokens(_modelId);
            int preTokens = _chat.EstimateConversationTokens(history, system);
            var compact = ConversationCompactor.Compact(
                history,
                preTokens,
                contextWindow,
                scaleThresholdsToWindow: isCustomEndpoint,
                keepRecentMessagesOverride: isCustomEndpoint ? EffortPolicy.KeepRecentMessages(_effort) : null);
            // Always apply trim; replace list when compacted or tool-spam was stripped.
            if (compact.Compacted || compact.Messages.Count != history.Count)
            {
                history.Clear();
                history.AddRange(compact.Messages);
                if (compact.Compacted)
                    onStatus?.Invoke("Compacted conversation history");
            }
            else
            {
                // Content-trimmed in place copies — sync if reference-equal lengths
                history.Clear();
                history.AddRange(compact.Messages);
            }

            string effectiveUser = sticky + userMessage;
            if (!string.IsNullOrWhiteSpace(goalBlock))
                effectiveUser += "\n\n" + goalBlock;
            if (!string.IsNullOrWhiteSpace(specialtyBlock))
                effectiveUser += "\n\n" + specialtyBlock;
            if (!string.IsNullOrWhiteSpace(planBlock))
                effectiveUser += "\n\n" + planBlock;
            if (!string.IsNullOrWhiteSpace(regression))
                effectiveUser += "\n\n" + regression;
            if (!string.IsNullOrWhiteSpace(fewShot))
                effectiveUser += "\n\n" + fewShot;
            if (!string.IsNullOrWhiteSpace(gitBlock))
                effectiveUser += "\n\n" + gitBlock;
            if (!string.IsNullOrWhiteSpace(repoMap))
                effectiveUser += "\n\n" + repoMap;
            if (!string.IsNullOrWhiteSpace(retrieval))
                effectiveUser += "\n\n" + retrieval;
            if (!string.IsNullOrWhiteSpace(kestralMem))
                effectiveUser += "\n\n" + kestralMem;
            if (!string.IsNullOrWhiteSpace(memory))
                effectiveUser += "\n\n" + memory;
            if (!string.IsNullOrWhiteSpace(workspaceBlock))
                effectiveUser += "\n\n" + workspaceBlock;

            // No aggregate size check existed here before -- for kestral, clamp the combined
            // per-turn blob to its real budget instead of the unchecked concatenation above
            // (see ContextBudget; downstream trimming exempts the current turn's content).
            if (isCustomEndpoint)
            {
                var blocks = new List<ContextBudget.Block>
                {
                    new("user-message", sticky + userMessage, 100),
                    new("goal", goalBlock, 95),
                    new("workspace-listing", workspaceBlock, 90),
                    new("kestral-memory", kestralMem, 70),
                    new("repo-retrieval", retrieval, 65),
                    new("repo-map", repoMap, 60),
                    new("plan-board", planBlock, 55),
                    new("project-memory", memory, 50),
                    new("git-branch", gitBlock, 45),
                    new("specialty", specialtyBlock, 40),
                    new("regression-guard", regression, 35),
                    new("few-shot", fewShot, 20),
                };
                int budget = ContextBudget.CharBudgetForContextWindow(_chat.GetApproximateContextWindowTokens(_modelId));
                effectiveUser = ContextBudget.EnforceBudget(blocks, budget);
            }

            int promptTokens = _chat.EstimateConversationTokens(
                new List<OpenRouterMessage>(history) { new("user", effectiveUser) },
                system);
            string finalText = string.Empty;
            int toolCalls = 0;

            _tools.BeginUndoTurn("agent");
            _tools.ClearWrittenPaths();
            try
            {
                var loop = new ToolCallingLoop(_chat, _tools, _modelId);
                ToolCallingResult result = await loop.RunAsync(
                    system,
                    effectiveUser,
                    onStatus,
                    cancellationToken,
                    AgentToolExecutor.ToolScope.Full,
                    onToolEvent: onToolEvent,
                    onToken: onToken,
                    gateForCustomEndpoint: isCustomEndpoint,
                    gatingMessage: userMessage,
                    conversationHistory: history,
                    effort: _effort);

                finalText = result.FinalText;
                toolCalls = result.ToolCallCount;
                cancelled = result.Cancelled;
                failed |= result.StreamInterrupted;

                // Regression guard reminder in final answer if failures remain
                string? regAfter = _tools.Workflow.RegressionGuardBlock();
                if (!string.IsNullOrWhiteSpace(regAfter) && toolCalls > 0
                    && !(finalText ?? "").Contains("REGRESSION", StringComparison.Ordinal))
                {
                    // soft: already injected on next turn
                }

                // A small model can narrate "I've made the change" without ever emitting a real
                // tool call, or it can echo the last tool observation back instead of answering --
                // give it exactly one explicit nudge before accepting nothing happened as done.
                // Distinctly, it can also do REAL work for the first step or two of a multi-step
                // plan and then narrate a false "done" claim for the rest instead of continuing to
                // call tools (observed directly: asked to create two files, it wrote the first for
                // real, then claimed the second was done without ever calling write_file again) --
                // the plan board (steps still Pending/Doing) catches that case.
                //
                // A fourth failure mode, live-reproduced with reasoning enabled (effort Medium+):
                // the model calls a read-only tool to investigate (e.g. read_file on a follow-up
                // like "I found a bug, please fix it"), then stops with zero further tool calls and
                // empty final text -- it never writes anything, but toolCalls is nonzero (the read
                // counted), so a toolCalls==0 check alone misses it entirely. The actual signal that
                // matters is simply "did requiresWrittenArtifacts end this turn with WrittenPaths
                // still empty" -- how many read-only tool calls happened along the way is beside the
                // point, so that's now the whole condition instead of an additional toolCalls==0 (or
                // echo) requirement layered on top of it.
                // The compact Kestrel tool menu intentionally omits plan_board. In that mode the
                // Builder cannot update plan state, so pending plan items are advisory rather than
                // proof of incomplete work. Disk artifacts and their validation remain the source
                // of truth for completion in every mode.
                var unfinishedSteps = planBoardAvailable
                    ? _tools.Workflow.Plan.Steps
                        .Where(s => s.Status is PlanStepStatus.Pending or PlanStepStatus.Doing)
                        .ToList()
                    : new List<PlanStep>();
                ArtifactQualitySnapshot? completionQuality = null;
                if (isCustomEndpoint && requiresWrittenArtifacts && _tools.WrittenPaths.Count > 0)
                {
                    completionQuality = ArtifactQualityInspector.Inspect(
                        _tools.WrittenPaths,
                        goal,
                        evidenceCharacterBudget: 2_000);
                }
                bool hasBlockingArtifactFailure = completionQuality != null
                    && ArtifactQualityInspector.HasBlockingFindings(completionQuality.Findings);

                // Effort scales genuine persistence on a stuck long-horizon task, not just how big a
                // single attempt's own budget is: Low/Medium keep the original one-retry behavior
                // (fail fast on a simple ask), High/Max keep attempting the SAME problem more times
                // before conceding it's actually stuck. Each attempt is a fresh generation, so
                // identical nudge wording can genuinely succeed on a later attempt after stalling on
                // an earlier one (live-confirmed: an attempt that stalled on read-only investigation
                // alone produced a real fix on the very next attempt) -- this isn't a loop that spins
                // forever, though: once EffortPolicy.MaxCompletionRetries is exhausted, it still
                // concedes and reports failure rather than retrying indefinitely.
                int maxCompletionRetries = isCustomEndpoint ? EffortPolicy.MaxCompletionRetries(_effort) : 1;
                int completionRetryAttempt = 0;
                while (isCustomEndpoint && requiresWrittenArtifacts && !cancelled && !failed
                    && (_tools.WrittenPaths.Count == 0
                        || hasBlockingArtifactFailure
                        || unfinishedSteps.Count > 0))
                {
                    completionRetryAttempt++;
                    string nudgeDetail = hasBlockingArtifactFailure
                        ? "The written artifacts are not a complete usable deliverable yet: "
                          + string.Join("; ", completionQuality!.Findings.Take(6))
                          + ". Read the affected files and use the required write tools to fix every listed issue now."
                        : unfinishedSteps.Count > 0
                        ? "You have NOT finished: " + string.Join("; ", unfinishedSteps.Select(s => $"{s.Index}. {s.Text}")) +
                          ". Call the required tool(s) for each of these now -- do not just describe them as done."
                        : "You MUST call write_file or str_replace now to make the requested change on disk — " +
                          "describing the change is not sufficient. Call a tool before responding.";
                    string attemptSuffix = maxCompletionRetries > 1 ? $" (attempt {completionRetryAttempt}/{maxCompletionRetries})" : string.Empty;
                    onStatus?.Invoke((hasBlockingArtifactFailure
                        ? "Written artifacts are incomplete — retrying with concrete validation findings"
                        : unfinishedSteps.Count > 0
                        ? $"{unfinishedSteps.Count} plan step(s) unfinished — retrying with explicit instruction"
                        : "No file changes made yet — retrying with explicit instruction") + attemptSuffix);
                    string nudgedInput = effectiveUser + "\n\n[INCOMPLETE] " + nudgeDetail;
                    ToolCallingResult retryResult = await loop.RunAsync(
                        system,
                        nudgedInput,
                        onStatus,
                        cancellationToken,
                        AgentToolExecutor.ToolScope.Full,
                        onToolEvent: onToolEvent,
                        onToken: onToken,
                        gateForCustomEndpoint: isCustomEndpoint,
                        gatingMessage: userMessage,
                        conversationHistory: history,
                        effort: _effort);

                    finalText = retryResult.FinalText;
                    toolCalls += retryResult.ToolCallCount;
                    cancelled = retryResult.Cancelled;
                    failed |= retryResult.StreamInterrupted;

                    unfinishedSteps = planBoardAvailable
                        ? _tools.Workflow.Plan.Steps
                            .Where(s => s.Status is PlanStepStatus.Pending or PlanStepStatus.Doing)
                            .ToList()
                        : new List<PlanStep>();
                    completionQuality = requiresWrittenArtifacts && _tools.WrittenPaths.Count > 0
                        ? ArtifactQualityInspector.Inspect(_tools.WrittenPaths, goal, evidenceCharacterBudget: 2_000)
                        : null;
                    hasBlockingArtifactFailure = completionQuality != null
                        && ArtifactQualityInspector.HasBlockingFindings(completionQuality.Findings);

                    if (completionRetryAttempt >= maxCompletionRetries)
                        break;
                }

                // These three cases mean every nudge-retry attempt this turn's effort tier allowed
                // still didn't produce a real fix -- the turn-level `failed` flag has to reflect
                // that, not just the appended warning text. Without this, ChatTui's turn-summary
                // status line (ActivityStatus.SummarizeTurn) only looks at toolCallCount and elapsed
                // time, so it kept showing "Task completed · Worked on N steps" even when nothing was
                // actually accomplished -- the warning text was easy to miss under a status line that
                // read as an unambiguous success. Live-reported: the model repeatedly claimed
                // completion on follow-up turns without making the requested change, and the status
                // line gave no visible signal anything had gone wrong.
                if (completionRetryAttempt > 0 && !cancelled && _tools.WrittenPaths.Count == 0)
                {
                    finalText = (finalText ?? string.Empty).TrimEnd()
                        + "\n\n⚠ No files were changed for this request — the model did not write to disk.";
                    failed = true;
                }
                else if (completionRetryAttempt > 0 && !cancelled && hasBlockingArtifactFailure)
                {
                    finalText = (finalText ?? string.Empty).TrimEnd()
                        + "\n\n⚠ The written artifacts still fail completion checks — review the reported validation findings.";
                    failed = true;
                }
                else if (completionRetryAttempt > 0 && !cancelled && unfinishedSteps.Count > 0)
                {
                    finalText = (finalText ?? string.Empty).TrimEnd()
                        + "\n\n⚠ Some plan steps were still not completed after retrying — check the plan board.";
                    failed = true;
                }

                string diagnostics = string.Empty;
                if (!cancelled && !failed
                    && _tools.Workflow.AutoDiagnosticsAfterWrite
                    && _tools.WrittenPaths.Count > 0
                    && _tools.ApprovalMode != ApprovalMode.Plan)
                {
                    diagnostics = await RunAutomaticDiagnosticsAsync(root, onStatus, cancellationToken);
                }

                // A compact model benefits from a separate evidence-backed verification pass.
                // This is shared across every implementation type: exact literals, structural
                // validation, file-type checks, diagnostics, and the full task contract.
                if (isCustomEndpoint && requiresWrittenArtifacts && !cancelled && !failed
                    && _tools.WrittenPaths.Count > 0
                    && _tools.ApprovalMode != ApprovalMode.Plan)
                {
                    ArtifactQualitySnapshot quality = ArtifactQualityInspector.Inspect(
                        _tools.WrittenPaths,
                        goal,
                        evidenceCharacterBudget: 8_000);
                    onStatus?.Invoke("Quality review · checking written artifacts");

                    string reviewInput = BuildQualityReviewInput(
                        userMessage,
                        goalBlock,
                        quality,
                        diagnostics);
                    var reviewLoop = new ToolCallingLoop(_chat, _tools, _modelId, maxRounds: 8);
                    ToolCallingResult reviewResult = await reviewLoop.RunAsync(
                        system,
                        reviewInput,
                        onStatus,
                        cancellationToken,
                        AgentToolExecutor.ToolScope.Full,
                        onToolEvent: onToolEvent,
                        onToken: null,
                        gateForCustomEndpoint: true,
                        gatingMessage: userMessage);

                    if (!string.IsNullOrWhiteSpace(reviewResult.FinalText))
                        finalText = reviewResult.FinalText;
                    toolCalls += reviewResult.ToolCallCount;
                    cancelled = reviewResult.Cancelled;
                    failed |= reviewResult.StreamInterrupted;

                    if (!cancelled)
                    {
                        ArtifactQualitySnapshot postQuality = ArtifactQualityInspector.Inspect(
                            _tools.WrittenPaths,
                            goal,
                            evidenceCharacterBudget: 2_000);
                        string postDiagnostics = diagnostics;
                        if (_tools.Workflow.AutoDiagnosticsAfterWrite && reviewResult.ToolCallCount > 0)
                        {
                            postDiagnostics = await RunAutomaticDiagnosticsAsync(root, onStatus, cancellationToken);
                        }

                        finalText = AppendUnresolvedQualityWarnings(
                            finalText,
                            postQuality.Findings,
                            postDiagnostics);
                    }
                }
                else if (DiagnosticsFailed(diagnostics))
                {
                    finalText = (finalText ?? string.Empty).TrimEnd()
                        + "\n\n--- auto diagnostics ---\n"
                        + (diagnostics.Length > 2500 ? diagnostics[..2500] + "\n..." : diagnostics);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                failed = true;
                onStatus?.Invoke("Stopped");
                finalText = string.IsNullOrEmpty(finalText)
                    ? "(Stopped by user.)"
                    : finalText + "\n\n(Stopped by user.)";
            }
            catch
            {
                failed = true;
                onStatus?.Invoke("Failed");
                throw;
            }
            finally
            {
                _tools.CommitUndoTurn();
                sw.Stop();
            }

            history.Add(new OpenRouterMessage("user", userMessage));
            history.Add(new OpenRouterMessage("assistant", finalText));

            if (isCustomEndpoint && _kestralMemory != null && !cancelled && !string.IsNullOrWhiteSpace(root))
            {
                try { _kestralMemory.RecordTurn(root, userMessage, finalText, criticSummary: null); }
                catch { /* best effort */ }
            }

            return new AgentTurnResult(
                finalText,
                sw.Elapsed,
                toolCalls,
                promptTokens,
                contextWindow,
                failed,
                cancelled,
                compact.BudgetWarning);
        }

        private async Task<string> RunAutomaticDiagnosticsAsync(
            string root,
            Action<string>? onStatus,
            CancellationToken cancellationToken)
        {
            try
            {
                onStatus?.Invoke("Self-check · diagnostics");
                string diagnostics = await DiagnosticsService.RunAsync(root, cancellationToken);
                NoteTestOutcomes(diagnostics, null);
                return diagnostics;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildQualityReviewInput(
            string userMessage,
            string goalBlock,
            ArtifactQualitySnapshot quality,
            string diagnostics)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[QUALITY VERIFICATION PASS]");
            sb.AppendLine("The first implementation pass is not automatically complete. Review the actual written artifacts against the source-of-truth contract.");
            sb.AppendLine("[ORIGINAL REQUEST]").AppendLine(userMessage).AppendLine();
            if (!string.IsNullOrWhiteSpace(goalBlock))
                sb.AppendLine(goalBlock).AppendLine();
            if (!string.IsNullOrWhiteSpace(quality.EvidenceBlock))
                sb.AppendLine(quality.EvidenceBlock).AppendLine();

            sb.AppendLine("[AUTOMATIC FINDINGS]");
            if (quality.Findings.Count == 0)
                sb.AppendLine("No deterministic structural issue was found. You must still check semantic fidelity and completeness.");
            else
                foreach (string finding in quality.Findings)
                    sb.AppendLine("- " + finding);

            if (!string.IsNullOrWhiteSpace(diagnostics))
                sb.AppendLine().AppendLine("[DIAGNOSTICS]").AppendLine(
                    diagnostics.Length > 3_000 ? diagnostics[..3_000] + "\n[...truncated]" : diagnostics);

            sb.AppendLine();
            sb.AppendLine("[REQUIRED ACTION]");
            sb.AppendLine("1. Check every R/C/L/A item against the actual files, preserving exact requested literals.");
            sb.AppendLine("2. Check completeness and type-appropriate quality. For human-facing interfaces check content fidelity, hierarchy, typography, spacing, alignment, asset integrity, responsive behavior, and interactions.");
            sb.AppendLine("3. Use tools now to fix every mismatch, broken reference, placeholder, invalid structure, or failed check. Do not rewrite unrelated work.");
            sb.AppendLine("4. If the files already pass, do not make cosmetic churn. Summarize the evidence you checked.");
            return sb.ToString();
        }

        private static bool DiagnosticsFailed(string diagnostics)
        {
            if (string.IsNullOrWhiteSpace(diagnostics))
                return false;
            if (diagnostics.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                || diagnostics.Contains("Fail:", StringComparison.OrdinalIgnoreCase))
                return true;

            var exitCode = System.Text.RegularExpressions.Regex.Match(
                diagnostics,
                @"(?im)\bexit_code:\s*(?<code>-?\d+)");
            if (exitCode.Success
                && int.TryParse(exitCode.Groups["code"].Value, out int code)
                && code != 0)
            {
                return true;
            }

            return System.Text.RegularExpressions.Regex.IsMatch(
                    diagnostics,
                    @"(?im)\berror\s+(?:[A-Z]{1,5}\d{2,6}|:)")
                || System.Text.RegularExpressions.Regex.IsMatch(
                    diagnostics,
                    @"(?im)\b[1-9]\d*\s+error(?:\(s\)|s)?\b");
        }

        private static string AppendUnresolvedQualityWarnings(
            string? finalText,
            IReadOnlyList<string> findings,
            string diagnostics)
        {
            var sb = new System.Text.StringBuilder((finalText ?? string.Empty).TrimEnd());
            if (findings.Count > 0)
            {
                sb.AppendLine().AppendLine()
                    .AppendLine("⚠ Automatic verification still found unresolved issues:");
                foreach (string finding in findings.Take(8))
                    sb.AppendLine("- " + finding);
            }

            if (DiagnosticsFailed(diagnostics))
            {
                sb.AppendLine().AppendLine("--- auto diagnostics ---")
                    .Append(diagnostics.Length > 2500 ? diagnostics[..2500] + "\n..." : diagnostics);
            }

            return sb.ToString();
        }

        private void NoteTestOutcomes(string output, string? filter)
        {
            if (string.IsNullOrWhiteSpace(output))
                return;
            bool failed = output.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Fail:", StringComparison.OrdinalIgnoreCase)
                || (output.Contains("exit_code: ", StringComparison.Ordinal)
                    && !output.Contains("exit_code: 0", StringComparison.Ordinal));
            if (failed)
            {
                string name = filter ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    // pull a short fingerprint
                    name = "diagnostics/tests";
                }
                _tools.Workflow.NoteFailedTest(name!);
            }
            else if (output.Contains("exit_code: 0", StringComparison.Ordinal)
                     || output.Contains("Passed!", StringComparison.OrdinalIgnoreCase))
            {
                _tools.Workflow.NoteTestsPassedClear(filter);
            }
        }

        // Deliberately narrow and cheap (no NLP, just shape): a trailing "?" or a leading
        // question-word covers the overwhelming majority of real questions ("why did you use X?",
        // "what does this do", "is that safe?") without trying to fully understand the sentence.
        // False negatives here just mean a genuine question gets treated as an action request (an
        // extra unwanted retry nudge, mildly annoying); false positives would mean a real follow-up
        // fix request gets exempted from the safety net (the actual bug this exists to catch), so
        // this deliberately leans toward under-excluding rather than over-excluding.
        // Public for direct regression testing (matches OpenRouterChatService.BuildChatRequest's
        // existing pattern of exposing otherwise-internal logic rather than adding a mocking seam).
        public static bool LooksLikePureQuestion(string message)
        {
            string trimmed = (message ?? string.Empty).TrimEnd();
            if (trimmed.Length == 0)
                return false;
            if (trimmed.EndsWith('?'))
                return true;

            string lower = trimmed.TrimStart().ToLowerInvariant();
            string[] questionStarts =
            [
                "why ", "what ", "what's ", "how ", "how's ", "is ", "is it", "are ", "can ", "can you",
                "could ", "could you", "should ", "does ", "do you", "did ", "will ", "would ", "which "
            ];
            return questionStarts.Any(s => lower.StartsWith(s, StringComparison.Ordinal));
        }

        // Deliberately depends ONLY on `mode` and `isCustomEndpoint` -- both fixed for the life of
        // a session (mode only changes on an explicit /mode command). Earlier this also took the
        // per-message-gated tool list and the per-message-detected task specialty, so the
        // rendered text (which tool names it enumerated, whether the dual-pass line was present)
        // changed on every single turn. The system prompt is always message zero, so any turn-to-
        // turn difference there breaks the inference server's prefix/KV-cache reuse and forces a
        // full reprocess of the *entire* accumulated conversation from token zero on every turn --
        // on a local model that cost scales with conversation length and is the dominant reason a
        // multi-turn session keeps feeling slow well past the first message. The specific tool
        // menu is still gated per message (see ToolGatingHeuristics) and still travels via the
        // request's own `tools` field -- this text just stops redundantly re-describing it.
        private static string BuildAgentSystemPrompt(ApprovalMode mode, bool isCustomEndpoint = false)
        {
            string approval = mode switch
            {
                ApprovalMode.Plan =>
                    "Approval mode is PLAN: do not mutate the workspace. Prefer search/read/diagnostics; " +
                    "mutating tools return Plan-only previews.\n",
                ApprovalMode.Ask =>
                    "Approval mode is ASK: the user may approve or deny write/shell/network actions.\n",
                _ =>
                    "Approval mode is AUTO inside the attached workspace sandbox.\n"
            };

            return
                "You are Axiom, a terminal coding agent with tools for shell, files, git, search, diagnostics, and downloads.\n" +
                approval +
                IntelligenceHelpers.DualPassInstruction + "\n" +
                IntelligenceHelpers.UncertaintyInstruction + "\n" +
                "When a message includes [[ATTACHED WORKSPACES — YOU HAVE ACCESS]], [[REPO MAP]], or [[PROJECT MEMORY]], " +
                "the user's local project is connected — use tools; never claim you lack access.\n" +
                "Use [[REPO MAP]] and [[REPO RETRIEVAL]] before blind searches when helpful.\n" +
                "Follow [[PROJECT MEMORY]] conventions when present (AXIOM.md / AGENTS.md).\n" +
                "Treat [[TASK CONTRACT]] R/C/L/A items as pass/fail requirements; preserve L literals verbatim.\n" +
                "Only the tools actually offered to you this turn are callable — the exact set varies by " +
                "message; if one you'd expect (e.g. apply_patch, plan_board) isn't offered right now, use the " +
                "closest available alternative instead of asking for it.\n" +
                "Prefer str_replace/apply_patch over full-file write_file when editing existing files.\n" +
                "For implementation tasks: inspect relevant files, implement the complete deliverable, reread every changed file, and run type-appropriate verification before claiming done. A scaffold is never a final result.\n" +
                "For human-facing interfaces, verify requested content, visual hierarchy, typography, spacing, alignment, asset integrity, responsive behavior, and interactions against the actual files.\n" +
                "When a [[PLAN BOARD]] is present, check off steps with plan_board as you finish them.\n" +
                "When [[REGRESSION GUARD]] lists failed tests, re-run them before claiming done.\n" +
                (isCustomEndpoint
                    ? "[EVIDENCE DISCIPLINE] Never state file contents, line/row counts, test or command " +
                      "output, or data analysis results from memory or inference — including summarizing a " +
                      "CSV/data file by estimating values instead of computing them. If a claim needs evidence " +
                      "you have not retrieved this turn, call the tool that produces it first; a plausible-" +
                      "sounding guess is a failure, not a shortcut. Same for arithmetic: never compute multi-" +
                      "step math yourself — call the calculator tool for any calculation with more than one " +
                      "operation or any numeric fact you state. Be concise in final answers.\n" +
                      "[NO CAPABILITY DISCLAIMERS] Never say you don't have the capability/ability to create " +
                      "files, run code, or manipulate the local environment, or that you are \"just a text-based " +
                      "AI\" — that is false: the tools listed above give you real, working access this turn. " +
                      "If you are unsure how to proceed, use a read/inspect tool to investigate, then act. A " +
                      "response that only explains what a human could do instead of doing it yourself is a failure.\n" +
                      "[INCREMENTAL WRITES] For a large or detailed deliverable (a full page with multiple " +
                      "sections, a sizeable script, anything you'd expect to run past a few hundred lines), do " +
                      "not attempt it as one single write_file call, and never call write_file with empty or " +
                      "placeholder content that you intend to fill in with a later call — if this connection " +
                      "is interrupted before that later call runs, an empty or stub file is what gets left " +
                      "behind, which is worse than not having written it at all. Instead, every write_file or " +
                      "str_replace call must leave the file it touches in a real, complete, usable state right " +
                      "then: write_file the first section (or a small standalone file) with its actual finished " +
                      "content, then use additional write_file or str_replace calls to append or add each " +
                      "remaining real section, each one leaving the file valid and non-empty on disk. One tool " +
                      "call generating a very large amount of content in a single response can exceed this " +
                      "connection's response window and fail outright — smaller calls of REAL content each " +
                      "complete safely and the file ends up just as complete either way, without ever passing " +
                      "through an empty or broken intermediate state.\n" +
                      "[SELF-SUFFICIENT SETUP] When a task needs a dependency, package, runtime, or tool that " +
                      "isn't present yet (a library the code imports, a CLI the build needs, a language runtime), " +
                      "install it yourself with run_shell (npm/pip/dotnet add, or the OS package manager for " +
                      "system-level software; use package_install/docker_run instead when they're offered this " +
                      "turn) rather than telling the user to install it or writing code that assumes it exists. " +
                      "This applies even when the request never used the word \"install\" — a broad task like " +
                      "\"build me a website\" that turns out to need a package is still your job to make actually " +
                      "runnable, not just scaffolded. The reverse matters just as much: only install what the " +
                      "task actually needs right now. Don't install a package, pull a container, or add a " +
                      "dependency speculatively because it might be useful later, and never reach for an install " +
                      "step as a first move before you've confirmed (by reading the code/config) that what you " +
                      "need is actually missing — an unnecessary install wastes the user's time and bandwidth " +
                      "and is not itself progress on the task.\n"
                    : "Prefer tools over guessing. Be concise in final answers.\n") +
                "For dangerous/destructive actions (rm -rf of large trees, force-push, dropping DBs), warn first.\n" +
                "When done, answer clearly with what changed and how to run/test it.";
        }
    }
}
