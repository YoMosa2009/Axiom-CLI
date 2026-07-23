using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Council;
using Axiom.Core.Workspace;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class CouncilOrchestratorTests
    {
        private const string CriticOk = "{\"status\":\"ok\",\"issues\":[]}";

        private static string CriticIssues(int count)
        {
            var issues = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) issues.Append(',');
                issues.Append($"{{\"severity\":\"medium\",\"summary\":\"issue {i}\",\"evidence\":\"e\",\"suggestedFix\":\"f\"}}");
            }
            return $"{{\"status\":\"issues\",\"issues\":[{issues}]}}";
        }

        private const string ValidPatch =
            "[[AXIOM_CODEBASE_PATCH]]\n" +
            "FILE: Program.cs\n" +
            "ACTION: edit\n" +
            "<<<<<<< SEARCH\n" +
            "old code\n" +
            "=======\n" +
            "new code\n" +
            ">>>>>>> REPLACE\n" +
            "[[END FILE]]\n" +
            "[[END AXIOM_CODEBASE_PATCH]]";

        [Fact]
        public async Task GeneralChatTask_NoIssues_SucceedsWithoutPatch()
        {
            var pipeline = new FakeChatPipeline("1. Do the thing", "Here is the answer.", CriticOk);
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Explain the thing", Workspace: null), progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Null(result.Patch);
            Assert.Equal("Here is the answer.", result.FinalText);
            Assert.Equal(3, pipeline.Calls.Count);
        }

        [Fact]
        public async Task WorkspaceTask_NoIssues_ReturnsParsedPatch()
        {
            var pipeline = new FakeChatPipeline("1. Edit Program.cs", ValidPatch, CriticOk);
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");
            var workspace = new ConnectedWorkspaceState { CodebaseEditAccessEnabled = true };

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Fix the bug", workspace), progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Patch);
            Assert.Single(result.Patch!.Files);
            Assert.Equal("Program.cs", result.Patch.Files[0].RelativePath);
        }

        [Fact]
        public async Task MinorIssues_TriggersTargetedPatch_ThenSucceeds()
        {
            var pipeline = new FakeChatPipeline(
                "1. Do the thing",      // architect
                "First answer",          // builder v1
                CriticIssues(2),         // critic: 2 issues -> targeted patch
                "Patched answer",        // builder v2 (targeted patch)
                CriticOk);               // critic: clean
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Do the thing", Workspace: null), progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("Patched answer", result.FinalText);
            Assert.Equal(5, pipeline.Calls.Count);
            Assert.Contains("TARGETED PATCH MODE", pipeline.Calls[3].SystemPrompt);
        }

        [Fact]
        public async Task ThreeOrMoreIssues_TriggersFullRevision_NotTargetedPatch()
        {
            var pipeline = new FakeChatPipeline(
                "1. Do the thing",
                "First answer",
                CriticIssues(3),         // 3 issues -> full revision
                "Rewritten answer",
                CriticOk);
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Do the thing", Workspace: null), progress: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("Rewritten answer", result.FinalText);
            Assert.DoesNotContain("TARGETED PATCH MODE", pipeline.Calls[3].SystemPrompt);
        }

        [Fact]
        public async Task RetryLimitReached_KeepsBestEffortOutput_DoesNotFail()
        {
            var pipeline = new FakeChatPipeline(
                "1. Do the thing",
                "Answer v1",
                CriticIssues(1),
                "Answer v2",
                CriticIssues(1),
                "Answer v3",
                CriticIssues(1)); // still has issues after MaxBuilderRetryAttempts (2)
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Do the thing", Workspace: null), progress: null, CancellationToken.None);

            Assert.True(result.Success); // best-effort: keeps output instead of failing the whole run
            Assert.Equal("Answer v3", result.FinalText);
            Assert.Equal(7, pipeline.Calls.Count); // architect + 3 builder + 3 critic
            Assert.True(result.FinalCriticReport.HasIssues);
        }

        // Regression guard for Phase 2 (round/retry scaling): the custom endpoint (kestral) must
        // give up after one retry instead of MaxBuilderRetryAttempts (2) -- dozens of round-trips
        // on a small local model reads as "endless looping" even though it's technically bounded.
        // eidos/hepha/"test-model" keep the original budget (see RetryLimitReached_* above).
        [Fact]
        public async Task RetryLimitReached_CustomEndpoint_StopsAfterOneRetry_NotTwo()
        {
            var pipeline = new FakeChatPipeline(
                "1. Do the thing",
                "Answer v1",
                CriticIssues(1),
                "Answer v2",
                CriticIssues(1)); // still has issues after the reduced 1-retry budget for kestral
            var orchestrator = new CouncilOrchestrator(pipeline, Axiom.Core.Chat.OpenRouterChatService.CustomEndpointModelId);

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Do the thing", Workspace: null), progress: null, CancellationToken.None);

            Assert.True(result.Success); // best-effort: keeps output instead of failing the whole run
            Assert.Equal("Answer v2", result.FinalText);
            Assert.Equal(5, pipeline.Calls.Count); // architect + 2 builder + 2 critic (vs. 7 for a real cloud model)
            Assert.True(result.FinalCriticReport.HasIssues);
        }

        [Fact]
        public async Task WorkspaceTask_UnparsablePatchAfterRetry_Fails()
        {
            var pipeline = new FakeChatPipeline(
                "1. Edit Program.cs",
                "This is not a valid patch.",
                "Still not a valid patch.");
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");
            var workspace = new ConnectedWorkspaceState { CodebaseEditAccessEnabled = true };

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Fix the bug", workspace), progress: null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Null(result.Patch);
            Assert.Equal(3, pipeline.Calls.Count); // architect + builder + one format-correction retry
        }
    }
}
