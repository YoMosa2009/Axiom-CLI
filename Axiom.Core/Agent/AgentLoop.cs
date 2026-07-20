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
        bool Cancelled = false);

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

            string system = FoundationSystemPrompt.Apply(BuildAgentSystemPrompt(_tools.ApprovalMode));
            string workspaceBlock = _workspace.BuildContextBlock();
            string memory = ProjectMemory.BuildContextBlock(_workspace.PrimaryRoot);
            string sticky = _tools.Workflow.ConsumeStickyPrefix() ?? string.Empty;
            string planBlock = _tools.Workflow.Plan.ToPromptBlock();
            string gitBlock = string.Empty;
            try
            {
                GitBranchSnapshot gitSnap = await GitBranchContext.CaptureAsync(
                    _workspace.PrimaryRoot, cancellationToken);
                gitBlock = GitBranchContext.ToPromptBlock(gitSnap);
            }
            catch { /* optional */ }

            string effectiveUser = sticky + userMessage;
            if (!string.IsNullOrWhiteSpace(planBlock))
                effectiveUser += "\n\n" + planBlock;
            if (!string.IsNullOrWhiteSpace(gitBlock))
                effectiveUser += "\n\n" + gitBlock;
            if (!string.IsNullOrWhiteSpace(memory))
                effectiveUser += "\n\n" + memory;
            if (!string.IsNullOrWhiteSpace(workspaceBlock))
                effectiveUser += "\n\n" + workspaceBlock;

            int contextWindow = _chat.GetApproximateContextWindowTokens(_modelId);
            int promptTokens = _chat.EstimateConversationTokens(
                new List<OpenRouterMessage>(history) { new("user", effectiveUser) },
                system);
            string finalText = string.Empty;
            int toolCalls = 0;

            _tools.BeginUndoTurn("agent");
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
                // Streamed tokens already delivered via onToken during generation; if model
                // returned tool-free text only through response.Text path, ensure flush.
                if (toolCalls == 0 && !string.IsNullOrEmpty(finalText) && onToken == null)
                {
                    // no-op
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
                cancelled);
        }

        private static string BuildAgentSystemPrompt(ApprovalMode mode)
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
                "When a message includes [[ATTACHED WORKSPACES — YOU HAVE ACCESS]] or [[PROJECT MEMORY]], " +
                "the user's local project is connected — use tools; never claim you lack access.\n" +
                "Follow [[PROJECT MEMORY]] conventions when present (AXIOM.md / AGENTS.md).\n" +
                "Tools include: write_file, str_replace (preferred for small edits), apply_patch, write_files, " +
                "read_file (offset/limit), list_dir, search_files, find_symbol, run_shell, " +
                "git_*, diagnostics, run_tests, package_install, docker_run, fetch_url, read_csv, read_notebook, " +
                "worktree_*, spawn_subagent, plan_board, run_background, open_pr, web_search.\n" +
                "Prefer str_replace/apply_patch over full-file write_file when editing existing files.\n" +
                "When a [[PLAN BOARD]] is present, check off steps with plan_board as you finish them.\n" +
                "Prefer tools over guessing. Be concise in final answers.\n" +
                "For dangerous/destructive actions (rm -rf of large trees, force-push, dropping DBs), warn first.\n" +
                "When done, answer clearly with what changed and how to run/test it.";
        }
    }
}
