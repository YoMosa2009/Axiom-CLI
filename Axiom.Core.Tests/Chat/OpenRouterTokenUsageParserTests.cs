using System.Text.Json;
using Axiom.Core.Chat;
using Xunit;

namespace Axiom.Core.Tests.Chat
{
    public class OpenRouterTokenUsageParserTests
    {
        [Fact]
        public void TryParse_OllamaUsageFields_ReturnsUsage()
        {
            using JsonDocument document = JsonDocument.Parse("{\"usage\":{\"prompt_eval_count\":123,\"eval_count\":45}}");

            OpenRouterTokenUsage? usage = OpenRouterTokenUsageParser.TryParse(document.RootElement);

            Assert.NotNull(usage);
            Assert.Equal(123, usage!.PromptTokens);
            Assert.Equal(45, usage.CompletionTokens);
            Assert.Equal(168, usage.TotalTokens);
        }

        [Fact]
        public void TryParse_TopLevelInputAndOutputTokens_ReturnsUsage()
        {
            using JsonDocument document = JsonDocument.Parse("{\"input_tokens\":80,\"output_tokens\":20}");

            OpenRouterTokenUsage? usage = OpenRouterTokenUsageParser.TryParse(document.RootElement);

            Assert.NotNull(usage);
            Assert.Equal(80, usage!.PromptTokens);
            Assert.Equal(20, usage.CompletionTokens);
        }
    }
}
