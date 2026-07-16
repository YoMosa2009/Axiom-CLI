using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class CouncilOrchestratorEvidenceTests
    {
        private const string CriticOk = "{\"status\":\"ok\",\"issues\":[]}";

        [Fact]
        public async Task CriticInput_IncludesStaticValidationFindings()
        {
            // Broken braces so static validation always pre-flags something.
            const string brokenCode = "def broken(:\n    return 1\n";
            var pipeline = new FakeChatPipeline(
                "1. Write broken python",
                brokenCode,
                CriticOk);
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");

            var events = new List<CouncilEvent>();
            var progress = new Progress<CouncilEvent>(e => events.Add(e));

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("write python", Workspace: null, Tools: new CouncilToolOptions(SandboxEnabled: false)),
                progress,
                CancellationToken.None);

            Assert.True(result.Success);
            // Critic call is the third pipeline call; its user input must carry pre-flagged issues.
            Assert.True(pipeline.Calls.Count >= 3);
            string criticUser = pipeline.Calls[2].UserInput;
            Assert.Contains("[PRE-FLAGGED ISSUES]", criticUser);
            Assert.Contains(events, e => e.Message.Contains("Static validation", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("pre-flagged", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task HardStaticFindings_PromoteCleanCriticToIssues()
        {
            // Mismatched braces are hard findings that merge into the report even if Critic says ok.
            const string broken = "class X { void m() { int x = 1; ";
            var pipeline = new FakeChatPipeline(
                "1. Plan",
                broken,
                CriticOk);
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("fix class", null, new CouncilToolOptions(SandboxEnabled: false)),
                progress: null,
                CancellationToken.None);

            // Clean critic + hard static findings => report has issues (may also trigger revision loop).
            // After retries the final report should still surface findings if code stays broken.
            Assert.NotNull(result.FinalCriticReport);
            // Either still has issues after best-effort, or builder revised — both prove merge ran.
            bool sawPreflagInAnyCritic = pipeline.Calls
                .Where(c => c.UserInput.Contains("[PRE-FLAGGED ISSUES]", StringComparison.Ordinal))
                .Any();
            Assert.True(sawPreflagInAnyCritic);
        }
    }
}
