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
    public const string QualifiedModelId = ProviderId + "/" + ModelId;
    public const string ApiKeyEnvironmentVariable = "AXIOM_KESTREL_API_KEY";
    public const int ContextWindowTokens = 135_168;
    // Kestrel still serves its full 135,168-token window. This lower client-side input
    // budget causes OpenCode to checkpoint before repeated project history makes each
    // subsequent coding turn unnecessarily slow.
    public const int OpenCodeInputBudgetTokens = 112_640;
    public const int MaxOutputTokens = 16_384;
    // Keep enough headroom for the checkpoint-generation call and the next substantive answer.
    // OpenCode compacts before sending a request that would consume this reserve, then rebuilds
    // the request from its checkpoint plus the retained recent turns.
    public const int CompactionReserveTokens = 16_384;
    public const int CompactionTailTurns = 6;
    // OpenCode 1.18.18 clamps this setting to 15,000 tokens.
    public const int CompactionRecentTokens = 15_000;
    public const int StreamStallTimeoutMilliseconds = 900_000;

    public static bool TryCreate(string? baseUrl, bool autoApprove, out string configJson, out string error)
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
        var permissions = new JsonObject
        {
            ["edit"] = autoApprove ? "allow" : "ask",
            ["bash"] = autoApprove ? "allow" : "ask",
            ["webfetch"] = "ask",
            ["websearch"] = "ask"
        };

        var root = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["model"] = QualifiedModelId,
            ["small_model"] = QualifiedModelId,
            ["permission"] = permissions,
            ["compaction"] = new JsonObject
            {
                ["auto"] = true,
                ["prune"] = true,
                ["tail_turns"] = CompactionTailTurns,
                ["preserve_recent_tokens"] = CompactionRecentTokens,
                ["reserved"] = CompactionReserveTokens
            },
            ["agent"] = new JsonObject
            {
                // Pin the compaction/checkpoint request to Kestrel too; the user never silently
                // falls back to another provider while a long coding session is being continued.
                ["compaction"] = new JsonObject
                {
                    ["model"] = QualifiedModelId
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
                    ["models"] = new JsonObject
                    {
                        [ModelId] = new JsonObject
                        {
                            ["name"] = "Kestrel 1 · OmniCoder-2-9B Q5_K_M",
                            ["limit"] = new JsonObject
                            {
                                ["context"] = ContextWindowTokens,
                                ["input"] = OpenCodeInputBudgetTokens,
                                ["output"] = MaxOutputTokens
                            }
                        }
                    }
                }
            }
        };

        configJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return true;
    }
}
