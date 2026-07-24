using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ToolCallingLoopTests
    {
        // Root-cause regression guard: confirmed live against granite3.2:8b (Kestral 1) — even with
        // real tool definitions on the wire, the model can fall back to a base-model "I'm just a
        // text-based AI" refusal instead of engaging with them. This is the exact text reported.
        [Fact]
        public void LooksLikeCapabilityDenial_DetectsReportedRefusalText()
        {
            const string reported =
                "Apologies for any misunderstanding. As an AI text-based model, I don't have the " +
                "capability to directly create or develop games, write code, or manipulate files in " +
                "your local environment. However, I can guide you through the process of setting up " +
                "a basic 2D retro-styled game using Phaser, a popular HTML5 game framework.";

            Assert.True(ToolCallingLoop.LooksLikeCapabilityDenial(reported));
        }

        [Theory]
        [InlineData("I don't have the capability to do that.")]
        [InlineData("I do not have the ability to run shell commands.")]
        [InlineData("As an AI text-based model, I cannot help with that directly.")]
        [InlineData("I'm not able to directly modify files on your machine.")]
        [InlineData("I don't have access to your local filesystem, so I can't do that.")]
        public void LooksLikeCapabilityDenial_DetectsCommonRefusalPhrasings(string text)
        {
            Assert.True(ToolCallingLoop.LooksLikeCapabilityDenial(text));
        }

        [Theory]
        [InlineData("I've written the file to disk and verified it compiles.")]
        [InlineData("Here is the implementation you asked for.")]
        [InlineData("The tests pass. I have the ability to run more if needed.")]
        [InlineData("")]
        [InlineData(null)]
        public void LooksLikeCapabilityDenial_DoesNotFlagNormalResponses(string? text)
        {
            Assert.False(ToolCallingLoop.LooksLikeCapabilityDenial(text!));
        }
    }
}
