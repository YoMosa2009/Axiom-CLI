using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class EffortPolicyTests
    {
        [Theory]
        [InlineData(EffortLevel.Low, false)]
        [InlineData(EffortLevel.Medium, true)]
        [InlineData(EffortLevel.High, true)]
        [InlineData(EffortLevel.Max, true)]
        public void ThinkEnabled_OnlyOffAtLow(EffortLevel level, bool expected)
        {
            Assert.Equal(expected, EffortPolicy.ThinkEnabled(level));
        }

        [Fact]
        public void MaxRounds_IncreaseMonotonicallyWithEffort()
        {
            int low = EffortPolicy.MaxRounds(EffortLevel.Low);
            int medium = EffortPolicy.MaxRounds(EffortLevel.Medium);
            int high = EffortPolicy.MaxRounds(EffortLevel.High);
            int max = EffortPolicy.MaxRounds(EffortLevel.Max);

            Assert.True(low < medium && medium < high && high < max,
                $"expected low < medium < high < max, got {low} < {medium} < {high} < {max}");
        }

        [Fact]
        public void MaxRounds_MediumMatchesPreEffortDefault()
        {
            // Backward-compatibility guarantee: any caller that doesn't pass an effort argument
            // (Council/subagent contexts) must see identical behavior to before effort mode existed.
            Assert.Equal(ToolCallingLoop.DefaultMaxRounds, EffortPolicy.MaxRounds(EffortLevel.Medium));
        }

        [Fact]
        public void MaxTokens_IncreaseMonotonicallyWithEffort()
        {
            int low = EffortPolicy.MaxTokens(EffortLevel.Low);
            int medium = EffortPolicy.MaxTokens(EffortLevel.Medium);
            int high = EffortPolicy.MaxTokens(EffortLevel.High);
            int max = EffortPolicy.MaxTokens(EffortLevel.Max);

            Assert.True(low < medium && medium < high && high < max,
                $"expected low < medium < high < max, got {low} < {medium} < {high} < {max}");
        }

        [Fact]
        public void MaxTokens_MediumMatchesPreEffortDefault()
        {
            Assert.Equal(8_192, EffortPolicy.MaxTokens(EffortLevel.Medium));
        }

        [Fact]
        public void MaxTokens_MaxStaysWellBelowContextCeiling()
        {
            // The whole point of this guard: Max should be meaningfully higher than High, but must
            // never approach the 131,072-token context window -- a single turn consuming the entire
            // window would leave nothing for prompt/history.
            int max = EffortPolicy.MaxTokens(EffortLevel.Max);
            Assert.True(max > EffortPolicy.MaxTokens(EffortLevel.High), "Max must exceed High");
            Assert.True(max < 131_072 / 2, "Max must stay well short of the context ceiling");
        }

        [Fact]
        public void KeepRecentMessages_IncreaseMonotonicallyWithEffort()
        {
            int low = EffortPolicy.KeepRecentMessages(EffortLevel.Low);
            int medium = EffortPolicy.KeepRecentMessages(EffortLevel.Medium);
            int high = EffortPolicy.KeepRecentMessages(EffortLevel.High);
            int max = EffortPolicy.KeepRecentMessages(EffortLevel.Max);

            Assert.True(low < medium && medium < high && high < max,
                $"expected low < medium < high < max, got {low} < {medium} < {high} < {max}");
        }

        [Theory]
        [InlineData("low", EffortLevel.Low)]
        [InlineData("LOW", EffortLevel.Low)]
        [InlineData("fast", EffortLevel.Low)]
        [InlineData("quick", EffortLevel.Low)]
        [InlineData("medium", EffortLevel.Medium)]
        [InlineData("med", EffortLevel.Medium)]
        [InlineData("default", EffortLevel.Medium)]
        [InlineData("normal", EffortLevel.Medium)]
        [InlineData("high", EffortLevel.High)]
        [InlineData("max", EffortLevel.Max)]
        [InlineData("maximum", EffortLevel.Max)]
        [InlineData("ultra", EffortLevel.Max)]
        public void TryParse_AcceptsAllDocumentedAliases(string input, EffortLevel expected)
        {
            bool ok = EffortPolicy.TryParse(input, out EffortLevel level);
            Assert.True(ok);
            Assert.Equal(expected, level);
        }

        [Fact]
        public void TryParse_UnknownInputFallsBackToMediumAndReturnsFalse()
        {
            bool ok = EffortPolicy.TryParse("ultra-mega-plus", out EffortLevel level);
            Assert.False(ok);
            Assert.Equal(EffortLevel.Medium, level);
        }

        [Fact]
        public void Label_RoundTripsThroughTryParse()
        {
            foreach (EffortLevel level in new[] { EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.Max })
            {
                string label = EffortPolicy.Label(level);
                Assert.True(EffortPolicy.TryParse(label, out EffortLevel parsed));
                Assert.Equal(level, parsed);
            }
        }
    }
}
