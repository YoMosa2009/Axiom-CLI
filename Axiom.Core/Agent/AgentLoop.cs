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
        bool Failed = false);

    // Multi-step tool-using chat turn: model may call shell/file/download tools repeatedly
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
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            bool failed = false;

            string system = FoundationSystemPrompt.Apply(BuildAgentSystemPrompt());
            string workspaceBlock = _workspace.BuildContextBlock();
            string effectiveUser = string.IsNullOrWhiteSpace(workspaceBlock)
                ? userMessage
                : userMessage + "\n\n" + workspaceBlock;

            int contextWindow = _chat.GetApproximateContextWindowTokens(_modelId);
            int promptTokens = _chat.EstimateConversationTokens(
                new List<OpenRouterMessage>(history) { new("user", effectiveUser) },
                system);
            string finalText = string.Empty;
            int toolCalls = 0;

            try
            {
                var loop = new ToolCallingLoop(_chat, _tools, _modelId);
                ToolCallingResult result = await loop.RunAsync(
                    system,
                    effectiveUser,
                    onStatus,
                    cancellationToken);

                finalText = result.FinalText;
                toolCalls = result.ToolCallCount;
                if (!string.IsNullOrEmpty(finalText))
                    onToken?.Invoke(finalText);
            }
            catch
            {
                failed = true;
                onStatus?.Invoke("Failed");
                throw;
            }
            finally
            {
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
                failed);
        }

        private static string BuildAgentSystemPrompt()
        {
            return
                "You are Axiom, a terminal coding agent with tools for shell, files, search, and downloads.\n" +
                "When a message includes [[ATTACHED WORKSPACES — YOU HAVE ACCESS]], the user's local folder " +
                "is connected: you DO have filesystem access inside those roots via tools. Never say you " +
                "cannot access their computer, project, or files while that block is present — call list_dir, " +
                "read_file, search_files, or run_shell instead.\n" +
                "Inspect the tree, run builds/tests, edit files, install packages, and download assets as needed.\n" +
                "Prefer tools over guessing file contents. Be concise in final answers.\n" +
                "For dangerous/destructive actions (rm -rf of large trees, force-push, dropping DBs), warn first.\n" +
                "When done, answer the user clearly with what changed and how to run it.";
        }
    }
}
