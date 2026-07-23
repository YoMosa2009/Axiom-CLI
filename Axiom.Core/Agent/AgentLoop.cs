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

        public AgentLoop(
            OpenRouterChatService chat,
            AgentToolExecutor tools,
            WorkspaceSession workspace,
            string modelId)
        {
            _chat = chat;
            _tools = tools;
            _workspace = workspace;
            _modelId = modelId;
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
            string goalBlock = goal.ToPromptBlock();
            // Same tool list ToolCallingLoop.RunAsync will independently (but deterministically)
            // resolve below -- computed here too so the system prompt's tool enumeration matches
            // what's actually offered instead of a static ~19-tool catalog. A genuine simplification
            // for every model: the filter is a no-op unless isCustomEndpoint is true.
            var toolsForPrompt = _tools.GetToolDefinitions(AgentToolExecutor.ToolScope.Full, userMessage, isCustomEndpoint);
            string system = FoundationSystemPrompt.Apply(BuildAgentSystemPrompt(_tools.ApprovalMode, specialty, toolsForPrompt, isCustomEndpoint));

            string workspaceBlock = _workspace.BuildContextBlock(isCustomEndpoint ? 40 : 120);
            string memory = ProjectMemory.BuildContextBlock(_workspace.PrimaryRoot);
            string sticky = _tools.Workflow.ConsumeStickyPrefix() ?? string.Empty;
            string planBlock = _tools.Workflow.Plan.ToPromptBlock();
            string repoMap = string.Empty;
            string retrieval = string.Empty;
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

            string fewShot = includeExpensiveContext ? IntelligenceHelpers.FewShotFromHistory(history, userMessage) : string.Empty;
            string? regression = _tools.Workflow.RegressionGuardBlock();
            string specialtyBlock = IntelligenceHelpers.SpecialtyPromptBlock(specialty);

            // Compact history for long sessions
            int contextWindow = _chat.GetApproximateContextWindowTokens(_modelId);
            int preTokens = _chat.EstimateConversationTokens(history, system);
            var compact = ConversationCompactor.Compact(history, preTokens, contextWindow);
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
            if (!string.IsNullOrWhiteSpace(memory))
                effectiveUser += "\n\n" + memory;
            if (!string.IsNullOrWhiteSpace(workspaceBlock))
                effectiveUser += "\n\n" + workspaceBlock;

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
                    gatingMessage: userMessage);

                finalText = result.FinalText;
                toolCalls = result.ToolCallCount;
                cancelled = result.Cancelled;

                // Regression guard reminder in final answer if failures remain
                string? regAfter = _tools.Workflow.RegressionGuardBlock();
                if (!string.IsNullOrWhiteSpace(regAfter) && toolCalls > 0
                    && !(finalText ?? "").Contains("REGRESSION", StringComparison.Ordinal))
                {
                    // soft: already injected on next turn
                }

                // A small model can narrate "I've made the change" without ever emitting a real
                // tool call (calls.Count stays 0, or it just echoes the last tool observation back)
                // -- give it exactly one explicit nudge before accepting nothing happened as done.
                if (isCustomEndpoint && looksLikeEdit && !cancelled
                    && _tools.WrittenPaths.Count == 0
                    && (toolCalls == 0 || result.LooksLikeObservationEcho))
                {
                    onStatus?.Invoke("No tool calls detected — retrying with explicit instruction");
                    string nudgedInput = effectiveUser +
                        "\n\n[NO TOOL CALLS DETECTED] You MUST call write_file or str_replace now to make " +
                        "the requested change on disk — describing the change is not sufficient. Call a tool before responding.";
                    ToolCallingResult retryResult = await loop.RunAsync(
                        system,
                        nudgedInput,
                        onStatus,
                        cancellationToken,
                        AgentToolExecutor.ToolScope.Full,
                        onToolEvent: onToolEvent,
                        onToken: onToken,
                        gateForCustomEndpoint: isCustomEndpoint,
                        gatingMessage: userMessage);

                    finalText = retryResult.FinalText;
                    toolCalls += retryResult.ToolCallCount;
                    cancelled = retryResult.Cancelled;

                    if (!cancelled && _tools.WrittenPaths.Count == 0 && retryResult.ToolCallCount == 0)
                    {
                        finalText = (finalText ?? string.Empty).TrimEnd()
                            + "\n\n⚠ No files were changed for this request — the model did not call any tools.";
                    }
                }

                string diagnostics = string.Empty;
                if (!cancelled
                    && _tools.Workflow.AutoDiagnosticsAfterWrite
                    && _tools.WrittenPaths.Count > 0
                    && _tools.ApprovalMode != ApprovalMode.Plan)
                {
                    diagnostics = await RunAutomaticDiagnosticsAsync(root, onStatus, cancellationToken);
                }

                // A compact model benefits from a separate evidence-backed verification pass.
                // This is shared across every implementation type: exact literals, structural
                // validation, file-type checks, diagnostics, and the full task contract.
                if (isCustomEndpoint && looksLikeEdit && !cancelled
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

        private static string BuildAgentSystemPrompt(
            ApprovalMode mode,
            TaskSpecialty specialty,
            IReadOnlyList<OpenRouterToolDefinition>? availableTools = null,
            bool isCustomEndpoint = false)
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

            string dual = specialty is TaskSpecialty.Review or TaskSpecialty.Docs or TaskSpecialty.General
                ? IntelligenceHelpers.DualPassInstruction + "\n"
                : "";

            // Verified directly against the real endpoint: a small self-hosted model reliably uses
            // its own native <tool_call>[...] format when told the EXACT syntax explicitly -- but
            // improvises a different, unparseable pseudo-format (code-fenced pseudo function calls,
            // prose descriptions, etc.) when only told "use your tools" without specifying how.
            string toolCallFormatInstruction = isCustomEndpoint
                ? "\nTo call a tool, respond with EXACTLY this format and nothing else: " +
                  "<tool_call>[{\"name\": \"tool_name\", \"arguments\": {\"key\": \"value\"}}]. " +
                  "Do not use code fences, Python syntax, or prose descriptions of the call — only that exact tag and JSON array."
                : "";

            return
                "You are Axiom, a terminal coding agent with tools for shell, files, git, search, diagnostics, and downloads.\n" +
                approval +
                dual +
                IntelligenceHelpers.UncertaintyInstruction + "\n" +
                "When a message includes [[ATTACHED WORKSPACES — YOU HAVE ACCESS]], [[REPO MAP]], or [[PROJECT MEMORY]], " +
                "the user's local project is connected — use tools; never claim you lack access.\n" +
                "Use [[REPO MAP]] and [[REPO RETRIEVAL]] before blind searches when helpful.\n" +
                "Follow [[PROJECT MEMORY]] conventions when present (AXIOM.md / AGENTS.md).\n" +
                "Treat [[TASK CONTRACT]] R/C/L/A items as pass/fail requirements; preserve L literals verbatim.\n" +
                "Tools available this turn: " +
                (availableTools is { Count: > 0 }
                    ? string.Join(", ", availableTools.Select(t => t.Name))
                    : "write_file, str_replace (preferred for small edits), apply_patch, write_files, " +
                      "read_file (offset/limit), list_dir, search_files, find_symbol, run_shell, " +
                      "git_*, diagnostics, run_tests, package_install, docker_run, fetch_url, read_csv, read_notebook, " +
                      "worktree_*, spawn_subagent, plan_board, run_background, open_pr, web_search") + "." +
                toolCallFormatInstruction + "\n" +
                "Prefer str_replace/apply_patch over full-file write_file when editing existing files.\n" +
                "For implementation tasks: inspect relevant files, implement the complete deliverable, reread every changed file, and run type-appropriate verification before claiming done. A scaffold is never a final result.\n" +
                "For human-facing interfaces, verify requested content, visual hierarchy, typography, spacing, alignment, asset integrity, responsive behavior, and interactions against the actual files.\n" +
                "When a [[PLAN BOARD]] is present, check off steps with plan_board as you finish them.\n" +
                "When [[REGRESSION GUARD]] lists failed tests, re-run them before claiming done.\n" +
                "Prefer tools over guessing. Be concise in final answers.\n" +
                "For dangerous/destructive actions (rm -rf of large trees, force-push, dropping DBs), warn first.\n" +
                "When done, answer clearly with what changed and how to run/test it.";
        }
    }
}
