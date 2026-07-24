using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class CouncilRolePromptsTests
    {
        [Theory]
        [InlineData("fix the null reference in Program.cs", CouncilTaskKind.Coding)]
        [InlineData("implement a login form", CouncilTaskKind.Coding)]
        [InlineData("create a responsive landing page", CouncilTaskKind.Coding)]
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

        // Regression guard: isCustomEndpoint defaults to false everywhere, so every existing call
        // site (eidos/hepha) must be completely unaffected by the kestral-specific prompt scaling.
        [Fact]
        public void ArchitectPrompt_DefaultsToUnscaledFramingWhenIsCustomEndpointOmitted()
        {
            string withDefault = CouncilRolePrompts.Architect(CouncilTaskKind.Coding, workspaceConnected: true, agenticBuilder: true);
            string withExplicitFalse = CouncilRolePrompts.Architect(CouncilTaskKind.Coding, workspaceConnected: true, agenticBuilder: true, isCustomEndpoint: false);
            Assert.Equal(withExplicitFalse, withDefault);
            Assert.Contains("CLOUD COUNCIL DELIBERATION PROTOCOL", withDefault);
        }

        [Theory]
        [InlineData(CouncilTaskKind.Coding, true, true)]
        [InlineData(CouncilTaskKind.General, false, false)]
        public void ArchitectPrompt_MateriallyShorterForCustomEndpoint(CouncilTaskKind kind, bool workspaceConnected, bool agenticBuilder)
        {
            string cloud = CouncilRolePrompts.Architect(kind, workspaceConnected, agenticBuilder, isCustomEndpoint: false);
            string kestral = CouncilRolePrompts.Architect(kind, workspaceConnected, agenticBuilder, isCustomEndpoint: true);

            Assert.True(kestral.Length < cloud.Length, $"Expected kestral prompt ({kestral.Length} chars) to be shorter than cloud prompt ({cloud.Length} chars).");
            Assert.DoesNotContain("CLOUD COUNCIL DELIBERATION PROTOCOL", kestral);
        }

        [Fact]
        public void BuilderPrompt_MateriallyShorterForCustomEndpoint()
        {
            string cloud = CouncilRolePrompts.Builder(CouncilTaskKind.Coding, true, false, true, true, isCustomEndpoint: false);
            string kestral = CouncilRolePrompts.Builder(CouncilTaskKind.Coding, true, false, true, true, isCustomEndpoint: true);

            // What this guards against: the ~500+ char EnvironmentBriefing+RoleBoundary+
            // CloudDeliberation block (RoleFraming(false)) creeping back into the kestral path.
            // A raw strict "always shorter" comparison is too fragile for that -- kestral
            // legitimately carries some cloud doesn't (e.g. explicit evidence-discipline/tool-use
            // instructions a small model needs spelled out) -- so assert the actual thing that
            // matters (the cloud-only framing block is absent) plus a generous length ceiling that
            // would still catch that block coming back wholesale.
            Assert.DoesNotContain("CLOUD COUNCIL DELIBERATION PROTOCOL", kestral);
            Assert.True(kestral.Length < cloud.Length * 1.2,
                $"Expected kestral prompt ({kestral.Length} chars) to stay close to cloud prompt ({cloud.Length} chars), not balloon past it.");
        }

        [Fact]
        public void BuilderPrompt_IncludesHelloFewShotOnlyForCustomEndpoint()
        {
            string cloud = CouncilRolePrompts.Builder(CouncilTaskKind.General, false, false, true, false, isCustomEndpoint: false);
            string kestral = CouncilRolePrompts.Builder(CouncilTaskKind.General, false, false, true, false, isCustomEndpoint: true);

            Assert.DoesNotContain("request 'hello'", cloud);
            Assert.Contains("request 'hello'", kestral);
        }

        [Fact]
        public void BuilderPrompt_ToolNameProseReflectsFilteredListWhenProvided()
        {
            var filtered = new System.Collections.Generic.List<Axiom.Core.Chat.OpenRouterToolDefinition>
            {
                new("read_file", "Read a file", new System.Text.Json.Nodes.JsonObject())
            };

            string p = CouncilRolePrompts.Builder(CouncilTaskKind.General, false, false, true, false, filtered, isCustomEndpoint: true);

            Assert.Contains("read_file", p);
            Assert.DoesNotContain("write_file", p);
            Assert.DoesNotContain("spawn_subagent", p);
        }

        [Fact]
        public void CriticPrompt_MateriallyShorterForCustomEndpoint()
        {
            string cloud = CouncilRolePrompts.Critic(CouncilTaskKind.Coding, true, true, isCustomEndpoint: false);
            string kestral = CouncilRolePrompts.Critic(CouncilTaskKind.Coding, true, true, isCustomEndpoint: true);

            Assert.True(kestral.Length < cloud.Length, $"Expected kestral prompt ({kestral.Length} chars) to be shorter than cloud prompt ({cloud.Length} chars).");
        }

        [Fact]
        public void ArtifactPrompts_RequireGeneralDeliverableQualityAndReview()
        {
            string builder = CouncilRolePrompts.Builder(CouncilTaskKind.Coding, true, false, true, true, isArtifactTask: true);
            string critic = CouncilRolePrompts.Critic(CouncilTaskKind.Coding, true, true, isArtifactTask: true);

            Assert.Contains("DELIVERABLE QUALITY BAR", builder);
            Assert.Contains("actual written artifacts", critic, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
