using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class CouncilRolePromptsTests
    {
        [Theory]
        [InlineData("fix the null reference in Program.cs", CouncilTaskKind.Coding)]
        [InlineData("implement a login form", CouncilTaskKind.Coding)]
        [InlineData("calculate the volume of a cylinder radius 5 height 10", CouncilTaskKind.Calculation)]
        [InlineData("explain how TCP handshakes work", CouncilTaskKind.Research)]
        [InlineData("hello there", CouncilTaskKind.General)]
        public void DetectTaskKind_ClassifiesCommonPrompts(string prompt, CouncilTaskKind expected)
        {
            Assert.Equal(expected, CouncilRolePrompts.DetectTaskKind(prompt));
        }

        [Fact]
        public void ArchitectPrompt_MentionsAgenticWhenEnabled()
        {
            string p = CouncilRolePrompts.Architect(CouncilTaskKind.Coding, workspaceConnected: true, agenticBuilder: true);
            Assert.Contains("AGENTIC BUILDER", p, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("write_file", p, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CriticPrompt_InspectToolsWhenEnabled()
        {
            string p = CouncilRolePrompts.Critic(CouncilTaskKind.Coding, workspaceConnected: true, agenticInspect: true);
            Assert.Contains("INSPECT TOOLS", p, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("read_file", p, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StripRoleMarkers_RemovesCompletionLines()
        {
            string raw = "1. Do the thing\nARCHITECT PLAN COMPLETE\n";
            string cleaned = CouncilRolePrompts.StripRoleMarkers(raw);
            Assert.DoesNotContain("ARCHITECT PLAN COMPLETE", cleaned);
            Assert.Contains("Do the thing", cleaned);
        }
    }
}
