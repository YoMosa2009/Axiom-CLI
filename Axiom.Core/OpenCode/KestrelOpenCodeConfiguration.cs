using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Axiom.Core.OpenCode;

/// <summary>
/// Builds the per-process OpenCode configuration used by Axiom's Kestrel bridge.
/// The API key is intentionally represented by an environment-variable reference:
/// no credential is written to an OpenCode config file.
/// </summary>
public static class KestrelOpenCodeConfiguration
{
    public const string DefaultBaseUrl = "https://ai.axiominference.work/v1";
    public const string ProviderId = "kestrel";
    public const string ModelId = "axiom/omnicoder-2-9b:q5_k_m";
    public const string GemmaModelId = "gemma4:12b";
    public const string QualifiedModelId = ProviderId + "/" + ModelId;
    public const string ApiKeyEnvironmentVariable = "AXIOM_KESTREL_API_KEY";
    public const int ContextWindowTokens = 262_144;
    // Reserve the final 16,384 tokens for the response. OpenCode checkpoints before the
    // remaining input would crowd out that reserve, then resumes from its compact checkpoint.
    public const int OpenCodeInputBudgetTokens = 245_760;
    public const int MaxOutputTokens = 16_384;
    // Keep enough headroom for the checkpoint-generation call and the next substantive answer.
    // OpenCode compacts before sending a request that would consume this reserve, then rebuilds
    // the request from its checkpoint plus the retained recent turns.
    public const int CompactionReserveTokens = 16_384;
    public const int CompactionTailTurns = 6;
    // OpenCode 1.18.18 clamps this setting to 15,000 tokens.
    public const int CompactionRecentTokens = 15_000;
    public const int StreamStallTimeoutMilliseconds = 900_000;

    public static bool TryCreate(
        string? baseUrl,
        bool autoApprove,
        out string configJson,
        out string error,
        int? activeContextWindowTokens = null,
        string? activeModelLabel = null,
        string? activeModelId = null)
    {
        configJson = string.Empty;
        error = string.Empty;

        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            error = "Kestrel requires an https:// endpoint. Run 'axiom connect' to configure it.";
            return false;
        }

        string normalizedBaseUrl = endpoint.AbsoluteUri.TrimEnd('/');
        string modelId = string.IsNullOrWhiteSpace(activeModelId)
            ? ModelId
            : activeModelId.Trim();
        string qualifiedModelId = ProviderId + "/" + modelId;
        int contextWindowTokens = activeContextWindowTokens is >= 2_048
            ? activeContextWindowTokens.Value
            : ContextWindowTokens;
        int outputTokens = Math.Min(MaxOutputTokens, Math.Max(1_024, contextWindowTokens / 4));
        int reserveTokens = Math.Min(CompactionReserveTokens, Math.Max(2_048, contextWindowTokens / 4));
        int inputTokens = Math.Max(2_048, contextWindowTokens - reserveTokens);
        int recentTokens = Math.Min(CompactionRecentTokens, inputTokens);
        int tailTurns = contextWindowTokens <= 16_384 ? 4 : CompactionTailTurns;
        string displayName = string.IsNullOrWhiteSpace(activeModelLabel)
            ? "Kestrel 1 · OmniCoder-2-9B Q5_K_M"
            : (modelId.Equals("gemma4:12b", StringComparison.OrdinalIgnoreCase)
                ? "Kestrel 1 Pro · "
                : "Kestrel 1 · ") + activeModelLabel;
        var permissions = new JsonObject
        {
            ["edit"] = autoApprove ? "allow" : "ask",
            ["bash"] = autoApprove ? "allow" : "ask",
            ["webfetch"] = "ask",
            ["websearch"] = "ask"
        };

        var modelCatalog = new JsonObject
        {
            [ModelId] = CreateModelDefinition("Kestrel 1 · OmniCoder-2-9B Q5_K_M", contextWindowTokens, inputTokens, outputTokens),
            [GemmaModelId] = CreateModelDefinition("Kestrel 1 Pro · Gemma 4 12B IT", contextWindowTokens, inputTokens, outputTokens)
        };
        if (!modelCatalog.ContainsKey(modelId))
            modelCatalog[modelId] = CreateModelDefinition(displayName, contextWindowTokens, inputTokens, outputTokens);

        var root = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["model"] = qualifiedModelId,
            ["small_model"] = qualifiedModelId,
            ["permission"] = permissions,
            ["compaction"] = new JsonObject
            {
                ["auto"] = true,
                ["prune"] = true,
                ["tail_turns"] = tailTurns,
                ["preserve_recent_tokens"] = recentTokens,
                ["reserved"] = reserveTokens
            },
            ["agent"] = new JsonObject
            {
                // Pin the compaction/checkpoint request to Kestrel too; the user never silently
                // falls back to another provider while a long coding session is being continued.
                ["compaction"] = new JsonObject
                {
                    ["model"] = qualifiedModelId
                }
            },
            ["provider"] = new JsonObject
            {
                [ProviderId] = new JsonObject
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["name"] = "Kestrel 1",
                    ["options"] = new JsonObject
                    {
                        ["baseURL"] = normalizedBaseUrl,
                        ["apiKey"] = "{env:" + ApiKeyEnvironmentVariable + "}",
                        // Kestrel can legitimately spend several minutes preparing a large
                        // prompt. Do not abort an active request solely because its total
                        // lifetime exceeds a client-side deadline.
                        ["timeout"] = false,
                        ["headerTimeout"] = false,
                        // Retain a finite escape hatch for a genuinely stalled stream.
                        ["chunkTimeout"] = StreamStallTimeoutMilliseconds
                    },
                    ["models"] = modelCatalog
                }
            }
        };

        configJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return true;
    }

    private static JsonObject CreateModelDefinition(string name, int context, int input, int output) =>
        new()
        {
            ["name"] = name,
            ["limit"] = new JsonObject
            {
                ["context"] = context,
                ["input"] = input,
                ["output"] = output
            }
        };
}
