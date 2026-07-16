using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
            bool failed = false;

            string system = FoundationSystemPrompt.Apply(BuildAgentSystemPrompt());
            string workspaceBlock = _workspace.BuildContextBlock();
            string effectiveUser = string.IsNullOrWhiteSpace(workspaceBlock)
                ? userMessage
                : userMessage + "\n\n" + workspaceBlock;

            var turnMessages = new List<OpenRouterMessage>(history)
            {
                new("user", effectiveUser, PreserveFullText: true)
            };

            IReadOnlyList<OpenRouterToolDefinition> toolDefs = _tools.GetToolDefinitions();
            int contextWindow = _chat.GetApproximateContextWindowTokens(_modelId);
            int promptTokens = _chat.EstimateConversationTokens(turnMessages, system);
            string finalText = string.Empty;

            try
            {
                for (int round = 0; round <= MaxToolRounds; round++)
                {
                    promptTokens = _chat.EstimateConversationTokens(turnMessages, system);
                    onStatus?.Invoke(round == 0 ? "Thinking" : round == 1 ? "Working" : $"Working · step {round + 1}");

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
                        onStatus?.Invoke("Generating response");
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
                        onStatus?.Invoke(DescribeToolStart(call));
                        string result = await _tools.ExecuteAsync(call.Name, call.ArgumentsJson, cancellationToken);
                        onStatus?.Invoke(DescribeToolDone(call, result));
                        turnMessages.Add(new OpenRouterMessage(
                            "tool",
                            result,
                            ToolCallId: call.Id,
                            PreserveFullText: true));
                    }

                    if (round == MaxToolRounds)
                    {
                        onStatus?.Invoke("Stopped");
                        if (!string.IsNullOrEmpty(finalText))
                            onToken?.Invoke(finalText);
                        break;
                    }
                }
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

        private static string DescribeToolStart(OpenRouterToolCall call)
        {
            string name = call.Name ?? string.Empty;
            string detail = ExtractDetail(name, call.ArgumentsJson);
            return name.ToLowerInvariant() switch
            {
                "run_shell" => string.IsNullOrWhiteSpace(detail) ? "Running command" : $"Running · {Truncate(detail, 48)}",
                "read_file" => string.IsNullOrWhiteSpace(detail) ? "Reading files" : $"Reading · {Truncate(detail, 48)}",
                "write_file" => string.IsNullOrWhiteSpace(detail) ? "Writing files" : $"Writing · {Truncate(detail, 48)}",
                "list_dir" => string.IsNullOrWhiteSpace(detail) ? "Listing files" : $"Listing · {Truncate(detail, 48)}",
                "download_file" => string.IsNullOrWhiteSpace(detail) ? "Downloading" : $"Downloading · {Truncate(detail, 48)}",
                "search_files" => string.IsNullOrWhiteSpace(detail) ? "Searching" : $"Searching · {Truncate(detail, 48)}",
                _ => $"Working · {name}"
            };
        }

        private static string DescribeToolDone(OpenRouterToolCall call, string result)
        {
            bool error = (result ?? string.Empty).StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || (result ?? string.Empty).Contains("exit_code: ", StringComparison.Ordinal)
                   && !(result ?? string.Empty).Contains("exit_code: 0", StringComparison.Ordinal);

            string name = call.Name ?? "tool";
            if (error)
                return $"Retrying · {name.Replace('_', ' ')} issue";

            return name.ToLowerInvariant() switch
            {
                "run_shell" => "Command finished",
                "read_file" => "Read complete",
                "write_file" => "Write complete",
                "list_dir" => "Listing complete",
                "download_file" => "Download complete",
                "search_files" => "Search complete",
                _ => "Tool finished"
            };
        }

        private static string ExtractDetail(string toolName, string? argsJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                JsonElement root = doc.RootElement;
                return toolName.ToLowerInvariant() switch
                {
                    "run_shell" => GetProp(root, "command"),
                    "read_file" or "write_file" => GetProp(root, "path"),
                    "list_dir" => GetProp(root, "path"),
                    "download_file" => GetProp(root, "url"),
                    "search_files" => GetProp(root, "query"),
                    _ => string.Empty
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetProp(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : string.Empty;

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "…";

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
