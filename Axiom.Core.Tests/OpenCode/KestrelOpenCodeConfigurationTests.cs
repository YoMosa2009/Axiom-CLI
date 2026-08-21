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
        Assert.Equal(135_168, KestrelOpenCodeConfiguration.ContextWindowTokens);
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
