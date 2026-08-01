using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class AgentLoopTests
    {
        [Theory]
        [InlineData("why did you use isinstance instead of type()?")]
        [InlineData("what does this function do")]
        [InlineData("is that actually safe?")]
        [InlineData("How does the retry logic work")]
        [InlineData("Can you explain this approach")]
        [InlineData("does this handle negative numbers?")]
        [InlineData("Which library did you use")]
        public void LooksLikePureQuestion_DetectsRealQuestions(string message)
        {
            Assert.True(AgentLoop.LooksLikePureQuestion(message));
        }

        [Theory]
        [InlineData("that's still not right, try again")]
        [InlineData("no, that's wrong")]
        [InlineData("fix the bug in add()")]
        [InlineData("please add input validation")]
        [InlineData("still broken")]
        [InlineData("try again")]
        public void LooksLikePureQuestion_DoesNotFlagActionRequests(string message)
        {
            // These are exactly the vague, keyword-free follow-ups that must still be treated as
            // requiring written artifacts -- reproduction: "that's still not right, try again" was
            // previously exempt from the retry-and-failure safety net entirely because it neither
            // matched an edit keyword nor looked like a question.
            Assert.False(AgentLoop.LooksLikePureQuestion(message));
        }
    }
}
