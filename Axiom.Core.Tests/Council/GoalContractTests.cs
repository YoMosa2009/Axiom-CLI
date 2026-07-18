using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class GoalContractTests
    {
        [Fact]
        public void FromPrompt_ExtractsBulletsAndCodingAcceptance()
        {
            string prompt = "Please implement login:\n- add Login form\n- wire submit button\nDo not change styles.";
            GoalContract c = GoalContract.FromPrompt(prompt);
            Assert.True(c.Requirements.Count >= 2);
            Assert.True(c.Acceptance.Count >= 1);
            string block = c.ToPromptBlock();
            Assert.Contains("TASK CONTRACT", block);
            Assert.Contains("R1", block);
        }
    }
}
