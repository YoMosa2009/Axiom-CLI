using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Axiom.Core.Tools;

namespace Axiom.Core.Agent
{
    public sealed record ToolCallingResult(
        string FinalText,
        int ToolCallCount,
        IReadOnlyList<string> ToolLog,
        bool Cancelled = false,
        bool LooksLikeObservationEcho = false);

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
            Action<string>? onToken = null,
            bool gateForCustomEndpoint = false,
            string? gatingMessage = null,
            IReadOnlyList<OpenRouterMessage>? conversationHistory = null)
        {
            // Keep the active conversation in the actual model message list. Previously callers
            // compacted history, but ToolCallingLoop discarded it and sent only the latest assembled
            // prompt. Follow-up requests therefore lost the user's prior constraints and decisions.
            var turnMessages = conversationHistory?
                .Where(message => message != null)
                .ToList()
                ?? new List<OpenRouterMessage>();
            turnMessages.Add(new OpenRouterMessage("user", userMessage, PreserveFullText: true));

            // Gate against the raw user request, not `userMessage` -- callers (AgentLoop, Council's
            // Builder) often pass a much larger assembled blob here (workspace context, repo map,
            // few-shot, etc.) whose incidental keywords would defeat the gating heuristics. Falls
            // back to `userMessage` for callers that don't have a separate raw message handy.
            IReadOnlyList<OpenRouterToolDefinition> toolDefs = _tools.GetToolDefinitions(scope, gatingMessage ?? userMessage, gateForCustomEndpoint);
            var allowedToolNames = new HashSet<string>(
                toolDefs.Where(tool => !string.IsNullOrWhiteSpace(tool.Name)).Select(tool => tool.Name),
                StringComparer.OrdinalIgnoreCase);
            var progressTracker = new ToolCallProgressTracker();
            var toolLog = new List<string>();
            int toolCalls = 0;
            string finalText = string.Empty;
            bool cancelled = false;
            int malformedToolCallRecoveries = 0;
            int consecutiveBlockedRounds = 0;
            int maxRounds = scope == AgentToolExecutor.ToolScope.Inspect
                ? Math.Min(_maxRounds, 6)
                : _maxRounds;
            int? maxTokensOverride = gateForCustomEndpoint
                ? scope == AgentToolExecutor.ToolScope.Full
                    ? Math.Clamp(_chat.GetApproximateContextWindowTokens(_modelId) / 4, 1_024, 3_072)
                    : Math.Clamp(_chat.GetApproximateContextWindowTokens(_modelId) / 6, 768, 1_536)
                : null;

            for (int round = 0; round < maxRounds; round++)
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
                    cancellationToken,
                    maxTokensOverride: maxTokensOverride);

                IReadOnlyList<OpenRouterToolCall> calls = response.ToolCalls ?? Array.Empty<OpenRouterToolCall>();
                finalText = !string.IsNullOrEmpty(response.Text) ? response.Text : collected.ToString();

                if (calls.Count == 0)
                {
                    // A local model can start a literal tool tag but truncate or malform the JSON.
                    // Do one protocol repair turn instead of treating the inert text as an answer.
                    if (malformedToolCallRecoveries == 0 && LooksLikeMalformedToolCallAttempt(finalText))
                    {
                        malformedToolCallRecoveries++;
                        turnMessages.Add(new OpenRouterMessage("assistant", finalText, PreserveFullText: true));
                        turnMessages.Add(new OpenRouterMessage(
                            "user",
                            "[TOOL PROTOCOL ERROR] Your last response looked like a tool call but could not be executed. " +
                            "Call exactly one offered tool with valid JSON arguments, or answer normally if no tool is needed. " +
                            "Do not describe a tool call as prose.",
                            PreserveFullText: true));
                        onStatus?.Invoke("Repairing malformed tool call");
                        continue;
                    }

                    onStatus?.Invoke("Generating response");
                    break;
                }

                turnMessages.Add(new OpenRouterMessage(
                    "assistant",
                    finalText,
                    ToolCalls: calls,
                    PreserveFullText: true));

                var runnable = new List<(OpenRouterToolCall Call, string Detail)>();
                foreach (OpenRouterToolCall call in calls)
                {
                    string detail = ExtractDetail(call.Name ?? string.Empty, call.ArgumentsJson);
                    if (!allowedToolNames.Contains(call.Name ?? string.Empty))
                    {
                        string feedback = "Tool protocol error: '" + (call.Name ?? "") + "' is not available this turn. " +
                            "Choose only from the offered tool list and use that tool's JSON schema.";
                        EmitToolFinished(call, detail, feedback, onToolEvent, onStatus, toolLog);
                        turnMessages.Add(new OpenRouterMessage("tool", feedback, ToolCallId: call.Id, PreserveFullText: true));
                        continue;
                    }

                    ToolActionDecision decision = progressTracker.Evaluate(call.Name, call.ArgumentsJson);
                    if (!decision.ShouldExecute)
                    {
                        string feedback = decision.Feedback!;
                        EmitToolFinished(call, detail, feedback, onToolEvent, onStatus, toolLog);
                        turnMessages.Add(new OpenRouterMessage("tool", feedback, ToolCallId: call.Id, PreserveFullText: true));
                        continue;
                    }

                    runnable.Add((call, detail));
                }

                if (runnable.Count == 0)
                {
                    consecutiveBlockedRounds++;
                    if (consecutiveBlockedRounds >= 2)
                    {
                        onStatus?.Invoke("Stopping repeated tool calls");
                        turnMessages.Add(new OpenRouterMessage(
                            "user",
                            "[TOOL LOOP GUARD] Repeated or unavailable tool calls were blocked because they cannot change the result. " +
                            "Do not call any more tools. Give the user an honest concise status using the completed tool results, " +
                            "including a concrete blocker if the requested work is not complete.",
                            PreserveFullText: true));
                        var finalCollected = new StringBuilder();
                        OpenRouterChatResponse finalResponse = await _chat.SendConversationStreamAsync(
                            turnMessages,
                            systemPrompt,
                            thinkingEnabled: false,
                            modelId: _modelId,
                            tools: null,
                            onToken: token =>
                            {
                                finalCollected.Append(token);
                                onToken?.Invoke(token);
                            },
                            cancellationToken,
                            maxTokensOverride: maxTokensOverride);
                        finalText = !string.IsNullOrEmpty(finalResponse.Text)
                            ? finalResponse.Text
                            : finalCollected.ToString();
                        break;
                    }

                    onStatus?.Invoke("Correcting repeated tool call");
                    continue;
                }

                consecutiveBlockedRounds = 0;

                // Parallel-safe reads can run concurrently; mutating tools stay serial.
                bool allParallelSafe = runnable.Count > 1
                    && runnable.All(item => AgentToolExecutor.IsParallelSafeTool(item.Call.Name ?? ""));

                if (allParallelSafe)
                {
                    onStatus?.Invoke($"Running {runnable.Count} tools in parallel");
                    foreach (var item in runnable)
                    {
                        onToolEvent?.Invoke(new ToolEvent(ToolEventPhase.Started, item.Call.Name, item.Detail));
                    }

                    var tasks = runnable.Select(async item =>
                    {
                        string result = await _tools.ExecuteAsync(
                            item.Call.Name, item.Call.ArgumentsJson, cancellationToken, scope);
                        return (item.Call, item.Detail, result);
                    }).ToList();

                    (OpenRouterToolCall call, string detail, string result)[] finished = await Task.WhenAll(tasks);
                    foreach (var (call, detail, resultRaw) in finished)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        toolCalls++;
                        string result = SecretRedaction.Redact(resultRaw);
                        progressTracker.RecordExecution(call.Name, call.ArgumentsJson, result);
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
                    foreach (var item in runnable)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        toolCalls++;
                        OpenRouterToolCall call = item.Call;
                        string detail = item.Detail;
                        onToolEvent?.Invoke(new ToolEvent(ToolEventPhase.Started, call.Name, detail));
                        onStatus?.Invoke(DescribeToolStart(call));

                        string result = SecretRedaction.Redact(await _tools.ExecuteAsync(
                            call.Name, call.ArgumentsJson, cancellationToken, scope));

                        progressTracker.RecordExecution(call.Name, call.ArgumentsJson, result);
                        EmitToolFinished(call, detail, result, onToolEvent, onStatus, toolLog);
                        turnMessages.Add(new OpenRouterMessage(
                            "tool",
                            result,
                            ToolCallId: call.Id,
                            PreserveFullText: true));
                    }
                }

                if (round == maxRounds - 1)
                {
                    onStatus?.Invoke("Tool round limit reached");
                    break;
                }
            }

            // Known small-model failure mode: instead of answering from a tool result, the model
            // just echoes the observation text back. Cheap, reusable check (also used by the
            // sub-1B intent router) so callers can fold it into their own "did this turn actually
            // produce something" verification (see Phase 4 write-verification retries).
            string toolContext = string.Join("\n", turnMessages.Where(m => m.Role == "tool").Select(m => m.Text));
            bool looksLikeEcho = LocalToolIntentRouter.IsToolObservationEcho(finalText, toolContext);

            return new ToolCallingResult(finalText, toolCalls, toolLog, cancelled, looksLikeEcho);
        }

        private static void EmitToolFinished(
            OpenRouterToolCall call,
            string detail,
            string? result,
            Action<ToolEvent>? onToolEvent,
            Action<string>? onStatus,
            List<string> toolLog)
        {
            bool isError = IsToolFailure(result);
            bool denied = (result ?? string.Empty).StartsWith("Denied:", StringComparison.OrdinalIgnoreCase)
                || (result ?? string.Empty).StartsWith("Plan-only:", StringComparison.OrdinalIgnoreCase);

            onToolEvent?.Invoke(new ToolEvent(
                denied ? ((result ?? string.Empty).StartsWith("Plan-only:", StringComparison.OrdinalIgnoreCase)
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

        public static string DescribeToolDone(OpenRouterToolCall call, string? result)
        {
            if ((result ?? string.Empty).StartsWith("Denied:", StringComparison.OrdinalIgnoreCase))
                return $"Denied · {(call.Name ?? "tool").Replace('_', ' ')}";
            if ((result ?? string.Empty).StartsWith("Plan-only:", StringComparison.OrdinalIgnoreCase))
                return $"Planned · {(call.Name ?? "tool").Replace('_', ' ')}";

            bool error = IsToolFailure(result);

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

        private static bool LooksLikeMalformedToolCallAttempt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("<tool_call", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<|tool_call|>", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<function=", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsToolFailure(string? result)
        {
            string text = result?.TrimStart() ?? string.Empty;
            return text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Tool error", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Tool protocol error", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Tool loop guard", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Unknown tool", StringComparison.OrdinalIgnoreCase)
                || (text.Contains("exit_code: ", StringComparison.Ordinal)
                    && !text.Contains("exit_code: 0", StringComparison.Ordinal));
        }

        private static string SummarizeResult(string? result)
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
