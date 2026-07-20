using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            TaskSpecialty specialty = IntelligenceHelpers.DetectSpecialty(userMessage);
            string system = FoundationSystemPrompt.Apply(BuildAgentSystemPrompt(_tools.ApprovalMode, specialty));

            string workspaceBlock = _workspace.BuildContextBlock();
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
                if (!string.IsNullOrWhiteSpace(root))
                {
                    onStatus?.Invoke("Mapping repo");
                    repoMap = RepoMapService.Build(root);
                    retrieval = RepoRetrievalService.Retrieve(root, userMessage);
                }
            }
            catch { /* optional */ }

            string fewShot = IntelligenceHelpers.FewShotFromHistory(history, userMessage);
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
                    onToken: onToken);

                finalText = result.FinalText;
                toolCalls = result.ToolCallCount;
                cancelled = result.Cancelled;

                // Builder-style self-check: diagnostics after writes (agent mode)
                if (!cancelled && _tools.Workflow.AutoDiagnosticsAfterWrite
                    && _tools.WrittenPaths.Count > 0
                    && _tools.ApprovalMode != ApprovalMode.Plan)
                {
                    try
                    {
                        onStatus?.Invoke("Self-check · diagnostics");
                        string diag = await DiagnosticsService.RunAsync(root, cancellationToken);
                        NoteTestOutcomes(diag, null);
                        if (!string.IsNullOrWhiteSpace(diag)
                            && (diag.Contains("error", StringComparison.OrdinalIgnoreCase)
                                || diag.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                                || diag.Contains("exit_code: 1", StringComparison.Ordinal)))
                        {
                            finalText = (finalText ?? "").TrimEnd()
                                + "\n\n--- auto diagnostics ---\n"
                                + (diag.Length > 2500 ? diag[..2500] + "\n..." : diag);
                        }
                    }
                    catch { /* optional */ }
                }

                // Regression guard reminder in final answer if failures remain
                string? regAfter = _tools.Workflow.RegressionGuardBlock();
                if (!string.IsNullOrWhiteSpace(regAfter) && toolCalls > 0
                    && !(finalText ?? "").Contains("REGRESSION", StringComparison.Ordinal))
                {
                    // soft: already injected on next turn
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
                string name = filter;
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

        private static string BuildAgentSystemPrompt(ApprovalMode mode, TaskSpecialty specialty)
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

            return
                "You are Axiom, a terminal coding agent with tools for shell, files, git, search, diagnostics, and downloads.\n" +
                approval +
                dual +
                IntelligenceHelpers.UncertaintyInstruction + "\n" +
                "When a message includes [[ATTACHED WORKSPACES — YOU HAVE ACCESS]], [[REPO MAP]], or [[PROJECT MEMORY]], " +
                "the user's local project is connected — use tools; never claim you lack access.\n" +
                "Use [[REPO MAP]] and [[REPO RETRIEVAL]] before blind searches when helpful.\n" +
                "Follow [[PROJECT MEMORY]] conventions when present (AXIOM.md / AGENTS.md).\n" +
                "Tools include: write_file, str_replace (preferred for small edits), apply_patch, write_files, " +
                "read_file (offset/limit), list_dir, search_files, find_symbol, run_shell, " +
                "git_*, diagnostics, run_tests, package_install, docker_run, fetch_url, read_csv, read_notebook, " +
                "worktree_*, spawn_subagent, plan_board, run_background, open_pr, web_search.\n" +
                "Prefer str_replace/apply_patch over full-file write_file when editing existing files.\n" +
                "When a [[PLAN BOARD]] is present, check off steps with plan_board as you finish them.\n" +
                "When [[REGRESSION GUARD]] lists failed tests, re-run them before claiming done.\n" +
                "Prefer tools over guessing. Be concise in final answers.\n" +
                "For dangerous/destructive actions (rm -rf of large trees, force-push, dropping DBs), warn first.\n" +
                "When done, answer clearly with what changed and how to run/test it.";
        }
    }
}
