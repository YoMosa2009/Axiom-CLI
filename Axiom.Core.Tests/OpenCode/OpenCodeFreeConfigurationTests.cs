using System.Text.Json.Nodes;
using Axiom.Core.OpenCode;

namespace Axiom.Core.Tests.OpenCode;

public sealed class OpenCodeFreeConfigurationTests
{
    [Fact]
    public void Create_UsesBuiltInProviderWithoutCredentialOrCustomProviderRegistration()
    {
        string config = OpenCodeFreeConfiguration.Create(
            OpenCodeFreeConfiguration.DefaultModelId,
            autoApprove: false,
            instructionsFilePath: "C:\\Axiom\\rules.md");

        JsonNode root = JsonNode.Parse(config)!;
        Assert.Equal(OpenCodeFreeConfiguration.DefaultModelId, root["model"]!.GetValue<string>());
        Assert.Equal(OpenCodeFreeConfiguration.DefaultModelId, root["small_model"]!.GetValue<string>());
        Assert.Null(root["provider"]);
        Assert.Equal("ask", root["permission"]!["edit"]!.GetValue<string>());
        Assert.Equal("C:\\Axiom\\rules.md", root["instructions"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void ModelId_AcceptsAnyQualifiedOpenCodeModelAndRejectsOtherProviders()
    {
        Assert.True(OpenCodeFreeConfiguration.IsModelId("opencode/new-free-model"));
        Assert.False(OpenCodeFreeConfiguration.IsModelId("kestrel/omnicoder"));
        Assert.False(OpenCodeFreeConfiguration.IsModelId("opencode/"));
    }
}
