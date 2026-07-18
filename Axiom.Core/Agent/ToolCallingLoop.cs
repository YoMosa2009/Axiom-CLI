using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;

namespace Axiom.Core.Agent
{
    public sealed record ToolCallingResult(
        string FinalText,
        int ToolCallCount,
        IReadOnlyList<string> ToolLog);

    // Bounded multi-round tool loop shared by AgentLoop and the council Builder so both paths
    // can create/edit files, run shell, and search — not only emit text into the chat.
    public sealed class ToolCallingLoop
    {
        public const int DefaultMaxRounds = 12;

        private readonly OpenRouterChatService _chat;
        private readonly AgentToolExecutor _tools;
        private readonly string _modelId;
        private readonly int _maxRounds;

        public ToolCallingLoop(
            OpenRouterChatService chat,
            AgentToolExecutor tools,
            string modelId,
            int maxRounds = DefaultMaxRounds)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _modelId = modelId;
            _maxRounds = Math.Clamp(maxRounds, 1, 24);
        }

        public async Task<ToolCallingResult> RunAsync(
            string systemPrompt,
            string userMessage,
            Action<string>? onStatus,
            CancellationToken cancellationToken,
            AgentToolExecutor.ToolScope scope = AgentToolExecutor.ToolScope.Full)
        {
            var turnMessages = new List<OpenRouterMessage>
            {
                new("user", userMessage, PreserveFullText: true)
            };

            IReadOnlyList<OpenRouterToolDefinition> toolDefs = _tools.GetToolDefinitions(scope);
            var toolLog = new List<string>();
            int toolCalls = 0;
            string finalText = string.Empty;
            int maxRounds = scope == AgentToolExecutor.ToolScope.Inspect
                ? Math.Min(_maxRounds, 6)
                : _maxRounds;

            for (int round = 0; round <= maxRounds; round++)
            {
                onStatus?.Invoke(round == 0
                    ? "Thinking"
                    : round == 1
                        ? "Working with tools"
                        : $"Working · tool step {round + 1}");

                var collected = new StringBuilder();
                OpenRouterChatResponse response = await _chat.SendConversationStreamAsync(
                    turnMessages,
                    systemPrompt,
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
                    string start = DescribeToolStart(call);
                    onStatus?.Invoke(start);
                    string result = await _tools.ExecuteAsync(call.Name, call.ArgumentsJson, cancellationToken, scope);
                    string done = DescribeToolDone(call, result);
                    onStatus?.Invoke(done);
                    toolLog.Add($"{call.Name}: {SummarizeResult(result)}");
                    turnMessages.Add(new OpenRouterMessage(
                        "tool",
                        result,
                        ToolCallId: call.Id,
                        PreserveFullText: true));
                }

                if (round == maxRounds)
                {
                    onStatus?.Invoke("Tool round limit reached");
                    break;
                }
            }

            return new ToolCallingResult(finalText, toolCalls, toolLog);
        }

        public static string DescribeToolStart(OpenRouterToolCall call)
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
                "web_search" => string.IsNullOrWhiteSpace(detail) ? "Searching web" : $"Web · {Truncate(detail, 48)}",
                _ => $"Working · {name}"
            };
        }

        public static string DescribeToolDone(OpenRouterToolCall call, string result)
        {
            bool error = (result ?? string.Empty).StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || ((result ?? string.Empty).Contains("exit_code: ", StringComparison.Ordinal)
                    && !(result ?? string.Empty).Contains("exit_code: 0", StringComparison.Ordinal));

            string name = call.Name ?? "tool";
            if (error)
                return $"Tool issue · {name.Replace('_', ' ')}";

            return name.ToLowerInvariant() switch
            {
                "run_shell" => "Command finished",
                "read_file" => "Read complete",
                "write_file" => "Write complete",
                "list_dir" => "Listing complete",
                "download_file" => "Download complete",
                "search_files" => "Search complete",
                "web_search" => "Web search complete",
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
                    "search_files" or "web_search" => GetProp(root, "query"),
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

        private static string SummarizeResult(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
                return "(empty)";
            string oneLine = result.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return Truncate(oneLine, 120);
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
