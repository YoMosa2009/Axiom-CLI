using System.Collections.Generic;
using System.Linq;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ConversationCompactorTests
    {
        private static List<OpenRouterMessage> MakeHistory(int turns)
        {
            var list = new List<OpenRouterMessage>();
            for (int i = 0; i < turns; i++)
            {
                list.Add(new OpenRouterMessage("user", $"message {i}"));
                list.Add(new OpenRouterMessage("assistant", $"response {i}"));
            }
            return list;
        }

        [Fact]
        public void Compact_DefaultThresholds_CompactsAtFixedCeilingRegardlessOfWindowSize()
        {
            var history = MakeHistory(20);

            // A window far larger than the fixed 48k ceiling: without scaling, the ceiling still
            // wins (cloud-model behavior must stay exactly as it always has been).
            var result = ConversationCompactor.Compact(
                history, estimatedTokens: 50_000, contextWindowTokens: 200_000, scaleThresholdsToWindow: false);

            Assert.True(result.Compacted);
        }

        [Fact]
        public void Compact_ScaledThresholds_DoesNotCompactBelowTheRealWindowsHardThreshold()
        {
            var history = MakeHistory(20);

            // 114688 * 70% ~= 80,281 -- well above the fixed 48k ceiling. 50k tokens must NOT
            // trigger compaction here even though it would under the unscaled default above; this
            // is the whole point of scaling to Kestral's real (much larger) window instead of the
            // ceiling tuned for its old, much smaller one.
            var result = ConversationCompactor.Compact(
                history, estimatedTokens: 50_000, contextWindowTokens: 114_688, scaleThresholdsToWindow: true);

            Assert.False(result.Compacted);
        }

        [Fact]
        public void Compact_ScaledThresholds_StillCompactsPastTheScaledHardThreshold()
        {
            var history = MakeHistory(20);

            var result = ConversationCompactor.Compact(
                history, estimatedTokens: 90_000, contextWindowTokens: 114_688, scaleThresholdsToWindow: true);

            Assert.True(result.Compacted);
        }

        [Fact]
        public void Compact_KeepRecentMessagesOverride_KeepsMoreRecentMessagesVerbatim()
        {
            var history = MakeHistory(20); // 40 messages

            var result = ConversationCompactor.Compact(
                history,
                estimatedTokens: 90_000,
                contextWindowTokens: 114_688,
                scaleThresholdsToWindow: true,
                keepRecentMessagesOverride: 16);

            Assert.True(result.Compacted);
            // 2 compacted-summary messages + 16 kept verbatim.
            Assert.Equal(18, result.Messages.Count);
            Assert.Equal("message 19", result.Messages.Last(m => m.Role == "user").Text);
        }
    }
}
