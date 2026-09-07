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
    // OpenCode 1.18.29 clamps this setting to 15,000 tokens (MAX_PRESERVE_RECENT_TOKENS).
    public const int CompactionRecentTokens = 15_000;
    public const int StreamStallTimeoutMilliseconds = 900_000;

    // OpenCode only sends a temperature when the model declares the capability (its provider
    // layer reads `capabilities.temperature`), so without both the flag on each catalog entry and
    // an agent-level value every request fell through to Ollama's per-model default -- 1.0 for
    // Gemma 4, and 0.8 for a model that ships no Modelfile parameters at all. That is far too
    // loose for a tool-calling loop: the model narrates its next step instead of calling the tool,
    // the turn ends with no tool call, and OpenCode correctly exits the loop. These values are
    // deliberately moderate rather than minimal -- Gemma's own model card asks for 1.0, and
    // pushing it very low is what triggers that family's repetition failure mode.
    public const double GemmaTemperature = 0.6;
    public const double OmniCoderTemperature = 0.3;
    public const double SamplingTopP = 0.95;

    // Kestrel's models are 9-12B. They cannot drive OpenCode's two delegation tools: measured
    // against a 26k-line repository, the model called `task` with a missing required `description`
    // more than ten times in a row -- every turn rejected by the schema, not one file read -- then
    // gave up and emitted a statement of intent. `skill` fails the same way, and picking
    // "customize-opencode" for a code-review request wastes a turn and floods a small model's
    // context with instructions for customizing OpenCode itself.
    //
    // Removing them is what makes the loop productive: the same prompt that stalled completes when
    // read/glob/grep are the only options. OpenCode drops a tool from the request when
    // `tools[name]` is false OR a permission denies it (session/llm/request.ts, resolveTools), so
    // both are set -- the permission additionally strips the skills catalog from the system prompt.
    public static readonly string[] DelegationTools = ["task", "skill"];

    public static bool TryCreate(
        string? baseUrl,
        bool autoApprove,
        out string configJson,
        out string error,
        int? activeContextWindowTokens = null,
        string? activeModelLabel = null,
        string? activeModelId = null,
        string? instructionsFilePath = null)
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
        // Gemma is a vision model on this server; OmniCoder is text-only. Declaring it lets
        // OpenCode attach images, which the proxy already translates into Ollama's native
        // base64 `images` field.
        bool isGemma = modelId.Equals(GemmaModelId, StringComparison.OrdinalIgnoreCase);
        string displayName = string.IsNullOrWhiteSpace(activeModelLabel)
            ? "Kestrel 1 · OmniCoder-2-9B Q5_K_M"
            : (isGemma ? "Kestrel 1 Pro · " : "Kestrel 1 · ") + activeModelLabel;
        double temperature = isGemma ? GemmaTemperature : OmniCoderTemperature;

        var permissions = new JsonObject
        {
            ["edit"] = autoApprove ? "allow" : "ask",
            ["bash"] = autoApprove ? "allow" : "ask",
            ["webfetch"] = "ask",
            ["websearch"] = "ask",
            // Denied rather than merely hidden: OpenCode gates the skills catalog in the system
            // prompt on this same permission, so denying it removes the temptation as well as the
            // tool. See DelegationTools for why.
            ["task"] = "deny",
            ["skill"] = "deny"
        };

        var modelCatalog = new JsonObject
        {
            [ModelId] = CreateModelDefinition("Kestrel 1 · OmniCoder-2-9B Q5_K_M", contextWindowTokens, inputTokens, outputTokens, vision: false),
            [GemmaModelId] = CreateModelDefinition("Kestrel 1 Pro · Gemma 4 12B IT", contextWindowTokens, inputTokens, outputTokens, vision: true)
        };
        if (!modelCatalog.ContainsKey(modelId))
            modelCatalog[modelId] = CreateModelDefinition(displayName, contextWindowTokens, inputTokens, outputTokens, vision: false);

        var root = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["model"] = qualifiedModelId,
            ["small_model"] = qualifiedModelId,
            ["permission"] = permissions,
            ["tools"] = CreateToolToggles(),
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
                },
                ["build"] = CreateAgentSampling(temperature),
                ["plan"] = CreateAgentSampling(temperature)
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

        if (!string.IsNullOrWhiteSpace(instructionsFilePath))
            root["instructions"] = new JsonArray(instructionsFilePath.Trim());

        configJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return true;
    }

    private static JsonObject CreateToolToggles()
    {
        var toggles = new JsonObject();
        foreach (string tool in DelegationTools)
            toggles[tool] = false;
        return toggles;
    }

    private static JsonObject CreateAgentSampling(double temperature) =>
        new()
        {
            ["temperature"] = temperature,
            ["top_p"] = SamplingTopP
        };

    private static JsonObject CreateModelDefinition(string name, int context, int input, int output, bool vision)
    {
        var definition = new JsonObject
        {
            ["name"] = name,
            // Without this OpenCode treats the model as temperature-incapable and drops the
            // agent temperature above, leaving Ollama's own (much hotter) default in charge.
            ["temperature"] = true,
            ["tool_call"] = true,
            ["attachment"] = vision,
            ["modalities"] = new JsonObject
            {
                ["input"] = vision ? new JsonArray("text", "image") : new JsonArray("text"),
                ["output"] = new JsonArray("text")
            },
            ["limit"] = new JsonObject
            {
                ["context"] = context,
                ["input"] = input,
                ["output"] = output
            }
        };
        return definition;
    }
}
