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
    }
}
