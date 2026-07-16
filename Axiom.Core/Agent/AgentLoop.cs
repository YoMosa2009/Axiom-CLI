using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
        int ContextWindowTokens);

    // Multi-step tool-using chat turn: model may call shell/file/download tools repeatedly
    // (bounded) before producing a final user-facing answer.
    public sealed class AgentLoop
    {
        private const int MaxToolRounds = 10;

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
            int toolCalls = 0;

            string system = FoundationSystemPrompt.Apply(BuildAgentSystemPrompt());
            string workspaceBlock = _workspace.BuildContextBlock();
            string effectiveUser = string.IsNullOrWhiteSpace(workspaceBlock)
                ? userMessage
                : userMessage + "\n\n" + workspaceBlock;

            var turnMessages = new List<OpenRouterMessage>(history)
            {
                new("user", effectiveUser, PreserveFullText: true)
            };

            IReadOnlyList<OpenRouterToolDefinition> toolDefs = AgentToolExecutor.GetToolDefinitions();
            int contextWindow = _chat.GetApproximateContextWindowTokens(_modelId);
            int promptTokens = _chat.EstimateConversationTokens(turnMessages, system);
            string finalText = string.Empty;

            for (int round = 0; round <= MaxToolRounds; round++)
            {
                promptTokens = _chat.EstimateConversationTokens(turnMessages, system);
                onStatus?.Invoke(round == 0 ? "Thinking..." : $"Agent step {round + 1}...");

                // Buffer tokens until we know whether this round ends in tool calls or final prose.
                var collected = new StringBuilder();
                OpenRouterChatResponse response = await _chat.SendConversationStreamAsync(
                    turnMessages,
                    system,
                    thinkingEnabled: false,
                    modelId: _modelId,
                    tools: toolDefs,
                    onToken: token => collected.Append(token),
                    cancellationToken);

                IReadOnlyList<OpenRouterToolCall> calls = response.ToolCalls ?? Array.Empty<OpenRouterToolCall>();
                finalText = !string.IsNullOrEmpty(response.Text) ? response.Text : collected.ToString();

                if (calls.Count == 0)
                {
                    if (!string.IsNullOrEmpty(finalText))
                        onToken?.Invoke(finalText);
                    break;
                }

                turnMessages.Add(new OpenRouterMessage(
                    "assistant",
                    finalText,
                    ToolCalls: calls,
                    PreserveFullText: true));

                foreach (OpenRouterToolCall call in calls)
                {
                    toolCalls++;
                    onStatus?.Invoke($"⚙ {call.Name}");
                    string result = await _tools.ExecuteAsync(call.Name, call.ArgumentsJson, cancellationToken);
                    onStatus?.Invoke($"✓ {call.Name}");
                    turnMessages.Add(new OpenRouterMessage(
                        "tool",
                        result,
                        ToolCallId: call.Id,
                        PreserveFullText: true));
                }

                if (round == MaxToolRounds)
                {
                    if (!string.IsNullOrEmpty(finalText))
                        onToken?.Invoke(finalText);
                    break;
                }
            }

            sw.Stop();

            history.Add(new OpenRouterMessage("user", userMessage));
            history.Add(new OpenRouterMessage("assistant", finalText));

            return new AgentTurnResult(
                finalText,
                sw.Elapsed,
                toolCalls,
                promptTokens,
                contextWindow);
        }

        private static string BuildAgentSystemPrompt()
        {
            return
                "You are Axiom, a terminal coding agent with tools for shell, files, search, and downloads.\n" +
                "When the user attaches workspaces, work inside them: inspect the tree, run builds/tests, " +
                "edit files, install packages, and download assets as needed.\n" +
                "Prefer tools over guessing file contents. Be concise in final answers.\n" +
                "For dangerous/destructive actions (rm -rf of large trees, force-push, dropping DBs), warn first.\n" +
                "When done, answer the user clearly with what changed and how to run it.";
        }
    }
}
