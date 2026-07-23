using Axiom.Core.Chat;
using Xunit;

namespace Axiom.Core.Tests.Chat
{
    public class OpenRouterChatServiceTests
    {
        [Fact]
        public void HasValidCustomEndpoint_TrueOnceAllThreeFieldsAreSet()
        {
            var service = new OpenRouterChatService();
            Assert.False(service.HasValidCustomEndpoint);

            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "llama3.1:8b");
            Assert.True(service.HasValidCustomEndpoint);
        }

        [Fact]
        public void HasValidCustomEndpoint_FalseForNonHttpsBaseUrl()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("http://ai.axiominference.work/v1", "test-key", "llama3.1:8b");

            Assert.False(service.HasValidCustomEndpoint);
        }

        [Fact]
        public void HasValidCustomEndpoint_FalseWhenAnyFieldIsBlank()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "", "llama3.1:8b");

            Assert.False(service.HasValidCustomEndpoint);
        }

        [Fact]
        public void HasAnyValidCloudCredential_TrueForCustomEndpointAloneWithNoOpenRouterKey()
        {
            var service = new OpenRouterChatService();
            Assert.False(service.HasAnyValidCloudCredential);

            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "llama3.1:8b");
            Assert.True(service.HasAnyValidCloudCredential);
        }

        [Fact]
        public void GetApproximateContextWindowTokens_ReturnsRealWindowForCustomEndpoint_NotFlooredTo32768()
        {
            var service = new OpenRouterChatService();

            int contextWindow = service.GetApproximateContextWindowTokens(OpenRouterChatService.CustomEndpointModelId);

            Assert.Equal(OpenRouterChatService.CustomEndpointContextWindowTokens, contextWindow);
            Assert.True(contextWindow < 32768);
        }

        [Fact]
        public void CustomEndpoint_UsesItsConfiguredContextWindowAcrossBudgeting()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint(
                "https://ai.axiominference.work/v1", "test-key", "granite3.2:8b", contextWindowTokens: 32768);

            Assert.Equal(32768, service.GetApproximateContextWindowTokens(OpenRouterChatService.CustomEndpointModelId));
            Assert.Equal(32768, service.GetInferenceSettingsSnapshot(OpenRouterChatService.CustomEndpointModelId).ContextWindowTokens);
            Assert.True(service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, 2048) > 28000);
        }

        [Fact]
        public void GetApproximateContextWindowTokens_StillFloorsRealOpenRouterAliases()
        {
            var service = new OpenRouterChatService();

            int contextWindow = service.GetApproximateContextWindowTokens(OpenRouterChatService.Eidos1ModelId);

            Assert.True(contextWindow >= 32768);
        }

        [Fact]
        public void IsSelectableModelAvailable_TrueForCustomEndpointRegardlessOfOpenRouterCatalogState()
        {
            var service = new OpenRouterChatService();

            Assert.True(service.IsSelectableModelAvailable(OpenRouterChatService.CustomEndpointModelId));
        }

        [Fact]
        public void ResolveModelLabel_ReturnsKestral1ForCustomEndpointAlias()
        {
            var service = new OpenRouterChatService();

            Assert.Equal(OpenRouterChatService.CustomEndpointModelLabel, service.ResolveModelLabel(OpenRouterChatService.CustomEndpointModelId));
        }

        [Fact]
        public void NormalizeSelectableModelId_RoundTripsCustomEndpointAlias()
        {
            var service = new OpenRouterChatService();

            Assert.Equal(OpenRouterChatService.CustomEndpointModelId, service.NormalizeSelectableModelId(OpenRouterChatService.CustomEndpointModelId));
        }

        // Root-cause regression guard: before this fix, the custom endpoint's prompt budget
        // always bottomed out at the hardcoded 2048-token emergency floor (8192 window - 8192
        // hardcoded completion reservation - margin - tools always went negative). A real fix
        // must leave meaningful room above that floor for an 8192-token window.
        [Fact]
        public void GetPromptTokenBudgetForModel_WellAboveEmergencyFloorForCustomEndpoint()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "granite3.2:8b");

            int budget = service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, maxCompletionTokens: 8192);

            Assert.True(budget > 4000, $"Expected budget well above the 2048 emergency floor, got {budget}.");
        }

        [Fact]
        public void GetPromptTokenBudgetForModel_UnaffectedByOversizedToolListForCustomEndpoint()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "granite3.2:8b");

            int budgetNoTools = service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, maxCompletionTokens: 1536);

            // A small custom-endpoint floor (512-1024) must still be respected even with a
            // realistic tool list pushing toolTokens up.
            var tools = new System.Collections.Generic.List<OpenRouterToolDefinition>
            {
                new("write_file", "Create or overwrite a file", new System.Text.Json.Nodes.JsonObject()),
                new("run_shell", "Run a shell command", new System.Text.Json.Nodes.JsonObject())
            };
            int budgetWithTools = service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, maxCompletionTokens: 1536, tools);

            Assert.True(budgetWithTools >= 512, $"Expected at least the custom-endpoint floor, got {budgetWithTools}.");
            Assert.True(budgetWithTools <= budgetNoTools, "Adding tool schemas should never increase the budget.");
        }

        // Regression guard: the budget-hardening fix must not change behavior for real OpenRouter
        // models, which already comfortably fit an 8192-token completion reservation inside a
        // 131k+-token window.
        [Theory]
        [InlineData(OpenRouterChatService.Eidos1ModelId)]
        [InlineData(OpenRouterChatService.Hepha1ModelId)]
        public void GetPromptTokenBudgetForModel_UnchangedForRealOpenRouterModels(string modelId)
        {
            var service = new OpenRouterChatService();

            int budget = service.GetPromptTokenBudgetForModel(modelId, maxCompletionTokens: 8192);

            // 131072 - 8192 - 1310 (contextWindow/100 safety margin) - 0 tool tokens = 121570.
            Assert.Equal(121570, budget);
        }
    }
}
