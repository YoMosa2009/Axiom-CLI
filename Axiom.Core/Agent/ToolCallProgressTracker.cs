using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Tracks whether a tool loop is making real progress. A model may repeat the same failed
    /// invocation indefinitely; the second identical action at the same workspace state is fed
    /// back as an actionable error instead of being executed again. A successful state-changing
    /// action advances the epoch, so rereading a file after editing it remains valid.
    /// </summary>
    public sealed class ToolCallProgressTracker
    {
        private readonly HashSet<string> _attemptedActions = new(StringComparer.Ordinal);
        private int _stateEpoch;
        private string? _lastSuccessfulStateChange;

        public ToolActionDecision Evaluate(string? toolName, string? argumentsJson)
        {
            string actionFingerprint = CreateFingerprint(toolName, argumentsJson);
            if (IsStateChanging(toolName)
                && string.Equals(_lastSuccessfulStateChange, actionFingerprint, StringComparison.Ordinal))
            {
                string repeatedName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
                return new ToolActionDecision(
                    false,
                    actionFingerprint,
                    $"Tool loop guard: {repeatedName} has the same arguments as the last successful state-changing action. " +
                    "Inspect the resulting state or make a different change; do not repeat an identical mutation.");
            }

            string fingerprint = CreateFingerprint(toolName, argumentsJson, _stateEpoch);
            if (_attemptedActions.Add(fingerprint))
                return new ToolActionDecision(true, fingerprint, null);

            string name = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
            return new ToolActionDecision(
                false,
                fingerprint,
                $"Tool loop guard: {name} with the same arguments was already attempted without any intervening workspace change. " +
                "Use the earlier result, correct the arguments, or choose a different tool; do not repeat this exact call.");
        }

        public void RecordExecution(string? toolName, string? argumentsJson, string? result)
        {
            if (IsStateChanging(toolName) && !IsFailure(result))
            {
                _stateEpoch++;
                _lastSuccessfulStateChange = CreateFingerprint(toolName, argumentsJson);
            }
        }

        public static string CreateFingerprint(string? toolName, string? argumentsJson, int stateEpoch = 0)
        {
            string name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
            return stateEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + name + ":" + CanonicalizeArguments(argumentsJson);
        }

        private static string CanonicalizeArguments(string? argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return "{}";

            try
            {
                using JsonDocument document = JsonDocument.Parse(argumentsJson);
                var builder = new StringBuilder();
                AppendCanonicalJson(document.RootElement, builder);
                return builder.ToString();
            }
            catch (JsonException)
            {
                return "invalid:" + argumentsJson.Trim();
            }
        }

        private static void AppendCanonicalJson(JsonElement element, StringBuilder builder)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    builder.Append('{');
                    bool firstProperty = true;
                    foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!firstProperty)
                            builder.Append(',');
                        firstProperty = false;
                        builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                        AppendCanonicalJson(property.Value, builder);
                    }
                    builder.Append('}');
                    break;
                case JsonValueKind.Array:
                    builder.Append('[');
                    bool firstItem = true;
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        if (!firstItem)
                            builder.Append(',');
                        firstItem = false;
                        AppendCanonicalJson(item, builder);
                    }
                    builder.Append(']');
                    break;
                default:
                    builder.Append(element.GetRawText());
                    break;
            }
        }

        private static bool IsStateChanging(string? toolName)
        {
            return toolName?.Trim().ToLowerInvariant() is
                "write_file" or "str_replace" or "apply_patch" or "write_files"
                or "run_shell" or "download_file" or "git_commit" or "git_checkout"
                or "worktree_create" or "worktree_remove" or "spawn_subagent" or "run_background"
                or "open_pr" or "package_install" or "docker_run" or "plan_board";
        }

        private static bool IsFailure(string? result)
        {
            string text = result?.TrimStart() ?? string.Empty;
            return text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Tool error", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Unknown tool", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Denied:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Plan-only:", StringComparison.OrdinalIgnoreCase)
                || (text.Contains("exit_code:", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("exit_code: 0", StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed record ToolActionDecision(bool ShouldExecute, string Fingerprint, string? Feedback);
}
