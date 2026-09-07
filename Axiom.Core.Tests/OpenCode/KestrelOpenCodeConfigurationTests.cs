using System.Text.Json.Nodes;
using Axiom.Core.OpenCode;

namespace Axiom.Core.Tests.OpenCode;

public sealed class KestrelOpenCodeConfigurationTests
{
    [Fact]
    public void TryCreate_UsesKestrelProviderAndNeverEmbedsCredential()
    {
        bool success = KestrelOpenCodeConfiguration.TryCreate(
            "https://ai.axiominference.work/v1/",
            autoApprove: false,
            out string json,
            out string error);

        Assert.True(success, error);
        Assert.DoesNotContain("sk-local-", json);

        JsonNode root = JsonNode.Parse(json)!;
        Assert.Equal(KestrelOpenCodeConfiguration.QualifiedModelId, root["model"]!.GetValue<string>());
        Assert.Equal("ask", root["permission"]!["edit"]!.GetValue<string>());
        Assert.Equal(
            "{env:" + KestrelOpenCodeConfiguration.ApiKeyEnvironmentVariable + "}",
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["options"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.ContextWindowTokens,
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["models"]![KestrelOpenCodeConfiguration.ModelId]!["limit"]!["context"]!.GetValue<int>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.OpenCodeInputBudgetTokens,
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["models"]![KestrelOpenCodeConfiguration.ModelId]!["limit"]!["input"]!.GetValue<int>());
        Assert.Equal(262_144, KestrelOpenCodeConfiguration.ContextWindowTokens);
        Assert.False(
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["options"]!["timeout"]!.GetValue<bool>());
        Assert.False(
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["options"]!["headerTimeout"]!.GetValue<bool>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.StreamStallTimeoutMilliseconds,
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["options"]!["chunkTimeout"]!.GetValue<int>());
        Assert.True(root["compaction"]!["auto"]!.GetValue<bool>());
        Assert.True(root["compaction"]!["prune"]!.GetValue<bool>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.CompactionReserveTokens,
            root["compaction"]!["reserved"]!.GetValue<int>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.CompactionRecentTokens,
            root["compaction"]!["preserve_recent_tokens"]!.GetValue<int>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.QualifiedModelId,
            root["agent"]!["compaction"]!["model"]!.GetValue<string>());
    }

    [Fact]
    public void TryCreate_UsesTheActiveGemmaModelInTheOpenCodeCatalog()
    {
        bool success = KestrelOpenCodeConfiguration.TryCreate(
            "https://ai.axiominference.work/v1/",
            autoApprove: false,
            out string json,
            out string error,
            activeContextWindowTokens: 262_144,
            activeModelLabel: "Gemma 4 12B IT",
            activeModelId: "gemma4:12b");

        Assert.True(success, error);
        JsonNode root = JsonNode.Parse(json)!;
        Assert.Equal("kestrel/gemma4:12b", root["model"]!.GetValue<string>());
        Assert.Equal("Kestrel 1 Pro · Gemma 4 12B IT",
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["models"]!["gemma4:12b"]!["name"]!.GetValue<string>());
        Assert.Equal("Kestrel 1 · OmniCoder-2-9B Q5_K_M",
            root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["models"]![KestrelOpenCodeConfiguration.ModelId]!["name"]!.GetValue<string>());
        Assert.Equal("kestrel/gemma4:12b", root["agent"]!["compaction"]!["model"]!.GetValue<string>());
    }

    [Fact]
    public void TryCreate_DeclaresSamplingSoOpenCodeDoesNotFallBackToOllamaDefaults()
    {
        bool success = KestrelOpenCodeConfiguration.TryCreate(
            "https://ai.axiominference.work/v1",
            autoApprove: false,
            out string json,
            out string error,
            activeModelId: KestrelOpenCodeConfiguration.GemmaModelId,
            activeModelLabel: "Gemma 4 12B IT");

        Assert.True(success, error);
        JsonNode root = JsonNode.Parse(json)!;
        JsonNode models = root["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["models"]!;

        // OpenCode drops the agent temperature unless the model declares the capability.
        Assert.True(models[KestrelOpenCodeConfiguration.GemmaModelId]!["temperature"]!.GetValue<bool>());
        Assert.True(models[KestrelOpenCodeConfiguration.ModelId]!["temperature"]!.GetValue<bool>());
        Assert.True(models[KestrelOpenCodeConfiguration.GemmaModelId]!["tool_call"]!.GetValue<bool>());

        Assert.Equal(
            KestrelOpenCodeConfiguration.GemmaTemperature,
            root["agent"]!["build"]!["temperature"]!.GetValue<double>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.SamplingTopP,
            root["agent"]!["build"]!["top_p"]!.GetValue<double>());
        Assert.Equal(
            KestrelOpenCodeConfiguration.GemmaTemperature,
            root["agent"]!["plan"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public void TryCreate_UsesTheOmniCoderTemperatureWhenOmniCoderIsServing()
    {
        bool success = KestrelOpenCodeConfiguration.TryCreate(
            "https://ai.axiominference.work/v1",
            autoApprove: false,
            out string json,
            out string error,
            activeModelId: KestrelOpenCodeConfiguration.ModelId);

        Assert.True(success, error);
        JsonNode root = JsonNode.Parse(json)!;
        Assert.Equal(
            KestrelOpenCodeConfiguration.OmniCoderTemperature,
            root["agent"]!["build"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public void TryCreate_MarksOnlyTheVisionModelAsAttachmentCapable()
    {
        bool success = KestrelOpenCodeConfiguration.TryCreate(
            "https://ai.axiominference.work/v1",
            autoApprove: false,
            out string json,
            out string error);

        Assert.True(success, error);
        JsonNode models = JsonNode.Parse(json)!["provider"]![KestrelOpenCodeConfiguration.ProviderId]!["models"]!;
        Assert.True(models[KestrelOpenCodeConfiguration.GemmaModelId]!["attachment"]!.GetValue<bool>());
        Assert.False(models[KestrelOpenCodeConfiguration.ModelId]!["attachment"]!.GetValue<bool>());

        JsonArray gemmaInput = models[KestrelOpenCodeConfiguration.GemmaModelId]!["modalities"]!["input"]!.AsArray();
        Assert.Contains(gemmaInput, item => item!.GetValue<string>() == "image");
        JsonArray omniInput = models[KestrelOpenCodeConfiguration.ModelId]!["modalities"]!["input"]!.AsArray();
        Assert.DoesNotContain(omniInput, item => item!.GetValue<string>() == "image");
    }

    [Fact]
    public void TryCreate_IncludesTheOperatingRulesFileOnlyWhenOneIsSupplied()
    {
        Assert.True(KestrelOpenCodeConfiguration.TryCreate(
            "https://ai.axiominference.work/v1",
            autoApprove: false,
            out string withoutRules,
            out string error));
        Assert.Null(JsonNode.Parse(withoutRules)!["instructions"]);

        Assert.True(KestrelOpenCodeConfiguration.TryCreate(
            "https://ai.axiominference.work/v1",
            autoApprove: false,
            out string withRules,
            out error,
            instructionsFilePath: "/axiom/rules.md"), error);
        JsonArray instructions = JsonNode.Parse(withRules)!["instructions"]!.AsArray();
        Assert.Equal("/axiom/rules.md", Assert.Single(instructions)!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://ai.axiominference.work/v1")]
    [InlineData("not a url")]
    [InlineData("")]
    public void TryCreate_RejectsNonHttpsEndpoint(string endpoint)
    {
        bool success = KestrelOpenCodeConfiguration.TryCreate(endpoint, autoApprove: false, out _, out string error);

        Assert.False(success);
        Assert.Contains("https", error);
    }
}
