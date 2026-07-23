using System;
using System.IO;
using System.Threading;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class KestralMemoryStoreTests : IDisposable
    {
        private readonly string _dataDir;
        private readonly string _workspaceDir;

        public KestralMemoryStoreTests()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "axiom-cli-kestral-mem-tests-" + Guid.NewGuid());
            _workspaceDir = Path.Combine(_dataDir, "workspace");
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_workspaceDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
        }

        private KestralMemoryStore CreateStore(long byteBudget = 10_000_000)
            => new(Path.Combine(_dataDir, "test.db"), byteBudget);

        [Fact]
        public void IngestAndRetrieve_SurfacesMatchingChunk()
        {
            File.WriteAllText(Path.Combine(_workspaceDir, "widget.cs"),
                "public class WidgetFactory\n{\n    public void BuildWidget() { }\n}\n");

            using var store = CreateStore();
            Assert.True(store.IsReady);

            store.IngestWorkspace(_workspaceDir);
            string result = store.Retrieve(_workspaceDir, "WidgetFactory BuildWidget");

            Assert.Contains("WidgetFactory", result, StringComparison.Ordinal);
            Assert.Contains("widget.cs", result, StringComparison.Ordinal);
        }

        [Fact]
        public void Retrieve_WithNoMatch_ReturnsEmpty()
        {
            File.WriteAllText(Path.Combine(_workspaceDir, "widget.cs"), "public class WidgetFactory { }\n");

            using var store = CreateStore();
            store.IngestWorkspace(_workspaceDir);

            string result = store.Retrieve(_workspaceDir, "completely unrelated zzqx term");
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void ReIngest_UnchangedFile_SkipsRewrite()
        {
            string path = Path.Combine(_workspaceDir, "widget.cs");
            File.WriteAllText(path, "public class WidgetFactory { }\n");

            using var store = CreateStore();
            store.IngestWorkspace(_workspaceDir);
            string firstResult = store.Retrieve(_workspaceDir, "WidgetFactory");

            // Re-ingest with no file changes at all -- content and mtime identical.
            store.IngestWorkspace(_workspaceDir);
            string secondResult = store.Retrieve(_workspaceDir, "WidgetFactory");

            Assert.Equal(firstResult, secondResult);
        }

        [Fact]
        public void ModifyFile_ReIngest_UpdatesOnlyThatFilesContent()
        {
            string pathA = Path.Combine(_workspaceDir, "a.cs");
            string pathB = Path.Combine(_workspaceDir, "b.cs");
            File.WriteAllText(pathA, "public class AlphaThing { }\n");
            File.WriteAllText(pathB, "public class BetaThing { }\n");

            using var store = CreateStore();
            store.IngestWorkspace(_workspaceDir);

            // Ensure a distinguishable mtime, then change content.
            Thread.Sleep(20);
            File.WriteAllText(pathA, "public class AlphaThingRenamed { }\n");
            store.IngestWorkspace(_workspaceDir);

            string alphaResult = store.Retrieve(_workspaceDir, "AlphaThingRenamed");
            string betaResult = store.Retrieve(_workspaceDir, "BetaThing");

            Assert.Contains("AlphaThingRenamed", alphaResult, StringComparison.Ordinal);
            Assert.Contains("BetaThing", betaResult, StringComparison.Ordinal);
        }

        [Fact]
        public void RecordTurn_ThenRetrieve_SurfacesPastConversation()
        {
            using var store = CreateStore();
            store.RecordTurn(_workspaceDir, "please rename FooBar to BazQux everywhere", "Renamed FooBar to BazQux in 3 files.", "clean");

            string result = store.Retrieve(_workspaceDir, "FooBar BazQux rename");

            Assert.Contains("PAST CONVERSATION", result, StringComparison.Ordinal);
            Assert.Contains("BazQux", result, StringComparison.Ordinal);
        }

        [Fact]
        public void EnforceByteBudget_EvictsLowestPriorityRowsUntilUnderBudget()
        {
            using var store = CreateStore(byteBudget: 1_000_000); // floor-clamped to 1MB minimum

            // Seed enough data to clearly exceed the 1MB floor (50 * 25,000 chars ~= 1.25MB),
            // so EnforceByteBudget below has real work to do rather than being a no-op.
            for (int i = 0; i < 50; i++)
            {
                string body = new string('x', 25_000);
                store.RecordTurn(_workspaceDir, $"turn {i} keyword{i}", body, null);
            }

            // RecordTurn already calls EnforceByteBudget internally; call again explicitly too.
            store.EnforceByteBudget();

            // Oldest, never-touched rows are evicted first (COALESCE(LastHitUtc, CreatedUtc) ASC),
            // so the earliest turn should be gone while the most recent one survives.
            string probeEarly = store.Retrieve(_workspaceDir, "keyword0");
            string probeLate = store.Retrieve(_workspaceDir, "keyword49");
            Assert.Equal(string.Empty, probeEarly);
            Assert.Contains("keyword49", probeLate, StringComparison.Ordinal);
        }
    }
}
