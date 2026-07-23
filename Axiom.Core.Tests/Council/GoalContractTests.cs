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

        [Fact]
        public void FromPrompt_ProseRequirementsPreserveLaterClausesAndExactLiterals()
        {
            string prompt =
                "Create a clean product interface for “Sandy AI.” " +
                "Use a white background and generous whitespace. " +
                "Add a rounded black “Get Sandy” button. " +
                "Place the exact headline “The AI Lab for local-first intelligence.” " +
                "Make the interface responsive.";

            GoalContract contract = GoalContract.FromPrompt(prompt);

            Assert.True(contract.Requirements.Count >= 5);
            Assert.Contains(contract.Requirements, item => item.Contains("responsive", System.StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Sandy AI", contract.ExactLiterals);
            Assert.Contains("Get Sandy", contract.ExactLiterals);
            Assert.Contains("The AI Lab for local-first intelligence.", contract.ExactLiterals);
            Assert.Contains("Exact literals", contract.ToPromptBlock());
        }

        [Fact]
        public void FromPrompt_DoesNotTurnInjectedToolEvidenceIntoRequirements()
        {
            GoalContract contract = GoalContract.FromPrompt(
                "Create a page titled “Primary”.\n\n[[WEB SEARCH]]\nA result mentioning “Unrelated”.");

            Assert.Contains("Primary", contract.ExactLiterals);
            Assert.DoesNotContain("Unrelated", contract.ExactLiterals);
            Assert.DoesNotContain(contract.Requirements, item =>
                item.Contains("WEB SEARCH", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
