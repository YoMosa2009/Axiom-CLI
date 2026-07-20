using System;
using System.Collections.Generic;
using System.Linq;
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
        IReadOnlyList<string> ToolLog,
        bool Cancelled = false);

    // Bounded multi-round tool loop shared by AgentLoop, council Builder/Critic, and subagents.
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
            AgentToolExecutor.ToolScope scope = AgentToolExecutor.ToolScope.Full,
            Action<ToolEvent>? onToolEvent = null,
            Action<string>? onToken = null)
        {
            var turnMessages = new List<OpenRouterMessage>
            {
                new("user", userMessage, PreserveFullText: true)
            };

            IReadOnlyList<OpenRouterToolDefinition> toolDefs = _tools.GetToolDefinitions(scope);
            var toolLog = new List<string>();
            int toolCalls = 0;
            string finalText = string.Empty;
            bool cancelled = false;
            int maxRounds = scope == AgentToolExecutor.ToolScope.Inspect
                ? Math.Min(_maxRounds, 6)
                : _maxRounds;

            for (int round = 0; round <= maxRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                    onToken: token =>
                    {
                        collected.Append(token);
                        onToken?.Invoke(token);
                    },
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

                // Parallel-safe reads can run concurrently; mutating tools stay serial.
                bool allParallelSafe = calls.Count > 1
                    && calls.All(c => AgentToolExecutor.IsParallelSafeTool(c.Name ?? ""));

                if (allParallelSafe)
                {
                    onStatus?.Invoke($"Running {calls.Count} tools in parallel");
                    foreach (OpenRouterToolCall call in calls)
                    {
                        string detail = ExtractDetail(call.Name, call.ArgumentsJson);
                        onToolEvent?.Invoke(new ToolEvent(ToolEventPhase.Started, call.Name, detail));
                    }

                    var tasks = calls.Select(async call =>
                    {
                        string result = await _tools.ExecuteAsync(
                            call.Name, call.ArgumentsJson, cancellationToken, scope);
                        return (call, result);
                    }).ToList();

                    (OpenRouterToolCall call, string result)[] finished = await Task.WhenAll(tasks);
                    foreach (var (call, resultRaw) in finished)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        toolCalls++;
                        string result = SecretRedaction.Redact(resultRaw);
                        string detail = ExtractDetail(call.Name, call.ArgumentsJson);
                        EmitToolFinished(call, detail, result, onToolEvent, onStatus, toolLog);
                        turnMessages.Add(new OpenRouterMessage(
                            "tool",
                            result,
                            ToolCallId: call.Id,
                            PreserveFullText: true));
                    }
                }
                else
                {
                    foreach (OpenRouterToolCall call in calls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        toolCalls++;
                        string detail = ExtractDetail(call.Name, call.ArgumentsJson);
                        onToolEvent?.Invoke(new ToolEvent(ToolEventPhase.Started, call.Name, detail));
                        onStatus?.Invoke(DescribeToolStart(call));

                        string result = SecretRedaction.Redact(await _tools.ExecuteAsync(
                            call.Name, call.ArgumentsJson, cancellationToken, scope));

                        EmitToolFinished(call, detail, result, onToolEvent, onStatus, toolLog);
                        turnMessages.Add(new OpenRouterMessage(
                            "tool",
                            result,
                            ToolCallId: call.Id,
                            PreserveFullText: true));
                    }
                }

                if (round == maxRounds)
                {
                    onStatus?.Invoke("Tool round limit reached");
                    break;
                }
            }

            return new ToolCallingResult(finalText, toolCalls, toolLog, cancelled);
        }

        private static void EmitToolFinished(
            OpenRouterToolCall call,
            string detail,
            string result,
            Action<ToolEvent>? onToolEvent,
            Action<string>? onStatus,
            List<string> toolLog)
        {
            bool isError = (result ?? string.Empty).StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || ((result ?? string.Empty).Contains("exit_code: ", StringComparison.Ordinal)
                    && !(result ?? string.Empty).Contains("exit_code: 0", StringComparison.Ordinal));
            bool denied = (result ?? string.Empty).StartsWith("Denied:", StringComparison.OrdinalIgnoreCase)
                || (result ?? string.Empty).StartsWith("Plan-only:", StringComparison.OrdinalIgnoreCase);

            onToolEvent?.Invoke(new ToolEvent(
                denied ? (result!.StartsWith("Plan-only:", StringComparison.OrdinalIgnoreCase)
                    ? ToolEventPhase.Planned
                    : ToolEventPhase.Denied)
                    : ToolEventPhase.Finished,
                call.Name,
                detail,
                SummarizeResult(result),
                isError));

            onStatus?.Invoke(DescribeToolDone(call, result));
            toolLog.Add($"{call.Name}: {SummarizeResult(result)}");
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
                "str_replace" => string.IsNullOrWhiteSpace(detail) ? "Patching file" : $"Patch · {Truncate(detail, 48)}",
                "apply_patch" => "Applying patch",
                "write_files" => "Writing files (batch)",
                "list_dir" => string.IsNullOrWhiteSpace(detail) ? "Listing files" : $"Listing · {Truncate(detail, 48)}",
                "download_file" => string.IsNullOrWhiteSpace(detail) ? "Downloading" : $"Downloading · {Truncate(detail, 48)}",
                "fetch_url" => string.IsNullOrWhiteSpace(detail) ? "Fetching URL" : $"Fetch · {Truncate(detail, 48)}",
                "search_files" => string.IsNullOrWhiteSpace(detail) ? "Searching" : $"Searching · {Truncate(detail, 48)}",
                "find_symbol" => string.IsNullOrWhiteSpace(detail) ? "Finding symbol" : $"Symbol · {Truncate(detail, 48)}",
                "web_search" => string.IsNullOrWhiteSpace(detail) ? "Searching web" : $"Web · {Truncate(detail, 48)}",
                "run_tests" => "Running tests",
                "package_install" => string.IsNullOrWhiteSpace(detail) ? "Installing package" : $"Install · {Truncate(detail, 40)}",
                "docker_run" => "Docker run",
                "read_csv" => "Reading CSV",
                "read_notebook" => "Reading notebook",
                "git_status" => "Git status",
                "git_diff" => "Git diff",
                "git_log" => "Git log",
                "git_commit" => string.IsNullOrWhiteSpace(detail) ? "Git commit" : $"Commit · {Truncate(detail, 40)}",
                "git_branch" => "Git branch",
                "diagnostics" => "Running diagnostics",
                "worktree_create" => "Creating worktree",
                "worktree_list" => "Listing worktrees",
                "worktree_remove" => "Removing worktree",
                "spawn_subagent" => string.IsNullOrWhiteSpace(detail) ? "Spawning subagent" : $"Subagent · {Truncate(detail, 40)}",
                _ => $"Working · {name}"
            };
        }

        public static string DescribeToolDone(OpenRouterToolCall call, string result)
        {
            if ((result ?? string.Empty).StartsWith("Denied:", StringComparison.OrdinalIgnoreCase))
                return $"Denied · {(call.Name ?? "tool").Replace('_', ' ')}";
            if ((result ?? string.Empty).StartsWith("Plan-only:", StringComparison.OrdinalIgnoreCase))
                return $"Planned · {(call.Name ?? "tool").Replace('_', ' ')}";

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
                "diagnostics" => "Diagnostics complete",
                "spawn_subagent" => "Subagent finished",
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
                    "run_shell" or "docker_run" => GetProp(root, "command"),
                    "read_file" or "write_file" or "str_replace" or "read_csv" or "read_notebook" => GetProp(root, "path"),
                    "list_dir" => GetProp(root, "path"),
                    "download_file" or "fetch_url" => GetProp(root, "url"),
                    "search_files" or "web_search" => GetProp(root, "query"),
                    "find_symbol" => GetProp(root, "symbol"),
                    "package_install" => GetProp(root, "ecosystem") + " " + GetProp(root, "package"),
                    "git_commit" => GetProp(root, "message"),
                    "spawn_subagent" => GetProp(root, "kind") + " " + GetProp(root, "task"),
                    "diagnostics" or "run_tests" => GetProp(root, "prefer"),
                    "worktree_create" => GetProp(root, "branch"),
                    "worktree_remove" => GetProp(root, "path"),
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
