using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
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
            Assert.Contains("[ARCHITECT PLAN]", pipeline.Calls[2].UserInput);
            Assert.Contains("1. Do the thing", pipeline.Calls[2].UserInput);
            Assert.Contains("TASK CONTRACT", pipeline.Calls[2].UserInput);
        }

        [Fact]
        public async Task CouncilRoles_ReceivePriorConversationAsModelHistory()
        {
            var pipeline = new FakeChatPipeline("1. Apply the earlier constraints", "Done", CriticOk);
            var orchestrator = new CouncilOrchestrator(pipeline, "test-model");
            var history = new[]
            {
                new OpenRouterMessage("user", "Use the name Atlas and keep the existing API."),
                new OpenRouterMessage("assistant", "Understood. I will preserve the API and use Atlas.")
            };

            CouncilResult result = await orchestrator.RunAsync(
                new CouncilRequest("Now implement the next step", Workspace: null, ConversationHistory: history),
                progress: null,
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Same(history, pipeline.Requests[0].ConversationHistory);
            Assert.Same(history, pipeline.Requests[1].ConversationHistory);
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

        // Regression guard for the Kestral Memory feature: a KestralMemoryStore wired into a
        // non-custom-endpoint orchestrator must be a true no-op (IsCustomEndpointModel gates every
        // access) -- proven by asserting the db never grows (schema creation alone writes some
        // bytes at construction time, so file-existence isn't the right signal; no growth means
        // no ingest/retrieve/record call ever happened).
        [Fact]
        public async Task WorkspaceTask_NonKestralModel_NeverTouchesKestralMemoryStore()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "axiom-cli-kmem-noop-root-" + Guid.NewGuid());
            string memDir = Path.Combine(Path.GetTempPath(), "axiom-cli-kmem-noop-mem-" + Guid.NewGuid());
            Directory.CreateDirectory(rootDir);
            Directory.CreateDirectory(memDir);
            string dbPath = Path.Combine(memDir, "kestral_memory.db");
            try
            {
                using var kestralMemory = new KestralMemoryStore(dbPath, byteBudget: 10_000_000);
                long sizeAfterConstruction = new FileInfo(dbPath).Length;

                var pipeline = new FakeChatPipeline("1. Edit Program.cs", ValidPatch, CriticOk);
                var orchestrator = new CouncilOrchestrator(
                    pipeline, "test-model", agentTools: null, kestralMemory: kestralMemory);
                var workspace = new ConnectedWorkspaceState
                {
                    CodebaseEditAccessEnabled = true,
                    RootPath = rootDir
                };

                CouncilResult result = await orchestrator.RunAsync(
                    new CouncilRequest("Fix the bug", workspace), progress: null, CancellationToken.None);

                Assert.True(result.Success);
                long sizeAfterRun = new FileInfo(dbPath).Length;
                Assert.Equal(sizeAfterConstruction, sizeAfterRun);
            }
            finally
            {
                try { Directory.Delete(rootDir, recursive: true); } catch { /* best effort */ }
                try { Directory.Delete(memDir, recursive: true); } catch { /* best effort */ }
            }
        }

        // End-to-end proof that Kestral Memory retrieval/recording actually flows through the real
        // RunAsync path (not just KestralMemoryStore's own isolated tests): a fact recorded in one
        // turn should surface in a later turn's Architect input via [[PAST CONVERSATION]].
        [Fact]
        public async Task WorkspaceTask_CustomEndpoint_RecallsPriorTurnFromKestralMemory()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "axiom-cli-kmem-recall-workspace-" + Guid.NewGuid());
            string memDir = Path.Combine(Path.GetTempPath(), "axiom-cli-kmem-recall-store-" + Guid.NewGuid());
            Directory.CreateDirectory(rootDir);
            Directory.CreateDirectory(memDir);
            string dbPath = Path.Combine(memDir, "kestral_memory.db");
            try
            {
                using var kestralMemory = new KestralMemoryStore(dbPath, byteBudget: 10_000_000);
                var workspace = new ConnectedWorkspaceState
                {
                    CodebaseEditAccessEnabled = true,
                    RootPath = rootDir
                };

                // Non-edit-shaped prompts (no "rename"/"fix"/"add"/etc.) so expectPatch stays false
                // with agentTools:null -- otherwise the run would bail out before ever reaching
                // RecordTurn, since a non-agentic edit-shaped turn requires a patch envelope.
                var firstPipeline = new FakeChatPipeline("1. Do the thing", "WidgetFactory builds GadgetWidget instances.", CriticOk);
                var firstOrchestrator = new CouncilOrchestrator(
                    firstPipeline, OpenRouterChatService.CustomEndpointModelId, agentTools: null, kestralMemory: kestralMemory);
                await firstOrchestrator.RunAsync(
                    new CouncilRequest("what does WidgetFactory build", workspace), progress: null, CancellationToken.None);

                var secondPipeline = new FakeChatPipeline("1. Do the thing", "Answer", CriticOk);
                var secondOrchestrator = new CouncilOrchestrator(
                    secondPipeline, OpenRouterChatService.CustomEndpointModelId, agentTools: null, kestralMemory: kestralMemory);
                await secondOrchestrator.RunAsync(
                    new CouncilRequest("what does WidgetFactory build", workspace), progress: null, CancellationToken.None);

                // Architect call (index 0) on the second run should have the first turn's outcome folded in.
                Assert.Contains("PAST CONVERSATION", secondPipeline.Calls[0].UserInput, StringComparison.Ordinal);
                Assert.Contains("GadgetWidget", secondPipeline.Calls[0].UserInput, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(rootDir, recursive: true); } catch { /* best effort */ }
                try { Directory.Delete(memDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
