using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Axiom.Core.OpenCode;

/// <summary>
/// Builds the isolated OpenCode configuration used for OpenCode's account-free provider.
/// The provider is built into OpenCode itself; deliberately do not add it to the custom
/// provider map or write any credential to disk.
/// </summary>
public static class OpenCodeFreeConfiguration
{
    public const string ProviderId = "opencode";
    public const string ModelPrefix = ProviderId + "/";
    public const string DefaultModelId = ModelPrefix + "big-pickle";

    public static readonly string[] SuggestedModelIds =
    [
        DefaultModelId,
        ModelPrefix + "mimo-v2.5-free",
        ModelPrefix + "ling-3.0-flash-fin-free",
        ModelPrefix + "nemotron-3-ultra-free",
        ModelPrefix + "nemotron-3.5-lightning-free",
        ModelPrefix + "muse-spark-1.3-contributor-free"
    ];

    public static bool IsModelId(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && modelId.Trim().StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase)
        && modelId.Trim().Length > ModelPrefix.Length;

    public static string NormalizeModelId(string modelId)
    {
        if (!IsModelId(modelId))
            throw new ArgumentException($"An OpenCode free model must start with '{ModelPrefix}'.", nameof(modelId));

        return modelId.Trim();
    }

    public static string Create(string modelId, bool autoApprove, string? instructionsFilePath = null)
    {
        string qualifiedModelId = NormalizeModelId(modelId);
        var root = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["model"] = qualifiedModelId,
            ["small_model"] = qualifiedModelId,
            ["permission"] = new JsonObject
            {
                ["edit"] = autoApprove ? "allow" : "ask",
                ["bash"] = autoApprove ? "allow" : "ask",
                ["webfetch"] = "ask",
                ["websearch"] = "ask"
            }
        };

        if (!string.IsNullOrWhiteSpace(instructionsFilePath))
            root["instructions"] = new JsonArray(instructionsFilePath.Trim());

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
