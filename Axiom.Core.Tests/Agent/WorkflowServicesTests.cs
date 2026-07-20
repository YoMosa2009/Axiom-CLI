using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class WorkflowServicesTests
    {
        [Fact]
        public void PlanBoard_ParsesNumberedSteps_AndMarksDone()
        {
            var board = new PlanBoard();
            board.SetFromArchitectPlan(
                "Here is the plan:\n" +
                "1. Create file\n" +
                "2. Add tests\n" +
                "3. Run build\n" +
                "Done.");

            Assert.True(board.HasSteps);
            Assert.Equal(3, board.Steps.Count);
            Assert.Equal("Create file", board.Steps[0].Text);
            Assert.True(board.TryMarkDone(1));
            Assert.Equal(PlanStepStatus.Done, board.Steps[0].Status);
            Assert.Contains("[x]", board.ToDisplayBlock());
            Assert.Contains("PLAN BOARD", board.ToPromptBlock());
        }

        [Fact]
        public void Sticky_ConsumesTurns_ThenClears()
        {
            var wf = new SessionWorkflowState();
            wf.SetSticky("fix login", 2);
            Assert.NotNull(wf.ConsumeStickyPrefix());
            Assert.Equal(1, wf.StickyTurnsRemaining);
            Assert.NotNull(wf.ConsumeStickyPrefix());
            Assert.Null(wf.ConsumeStickyPrefix());
            Assert.True(string.IsNullOrEmpty(wf.StickyTask));
        }

        [Fact]
        public void TurnChangeTracker_PartialReject_RestoresFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-tc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string a = Path.Combine(dir, "a.txt");
            string b = Path.Combine(dir, "b.txt");
            File.WriteAllText(a, "old-a");
            try
            {
                var t = new TurnChangeTracker();
                t.BeginTurn();
                t.NoteBeforeWrite(a, "old-a", existed: true);
                File.WriteAllText(a, "new-a");
                t.NoteAfterWrite(a, "new-a");
                t.NoteBeforeWrite(b, null, existed: false);
                File.WriteAllText(b, "new-b");
                t.NoteAfterWrite(b, "new-b");

                Assert.Equal(2, t.Files.Count);
                t.Reject(new[] { 1 });
                Assert.Equal("old-a", File.ReadAllText(a));
                Assert.True(File.Exists(b));
                t.RejectAll();
                Assert.False(File.Exists(b));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void CheckpointStore_CreateAndRestore()
        {
            string storeDir = Path.Combine(Path.GetTempPath(), "axiom-cp-" + Guid.NewGuid().ToString("N"));
            string workDir = Path.Combine(Path.GetTempPath(), "axiom-cpw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(storeDir);
            Directory.CreateDirectory(workDir);
            string file = Path.Combine(workDir, "x.txt");
            File.WriteAllText(file, "v1");
            try
            {
                var store = new CheckpointStore(storeDir);
                string id = store.CreateFromPaths("snap", workDir, new[] { file });
                Assert.False(string.IsNullOrWhiteSpace(id));
                File.WriteAllText(file, "v2");
                string result = store.Restore(id);
                Assert.Contains("restored", result, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("v1", File.ReadAllText(file));
                Assert.True(store.List().Count >= 1);
            }
            finally
            {
                try { Directory.Delete(storeDir, true); } catch { /* ignore */ }
                try { Directory.Delete(workDir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ToolReplayLog_RecordsMutatingOnly_AndDescribes()
        {
            var log = new ToolReplayLog();
            log.BeginTurn();
            log.Record("read_file", "{\"path\":\"a\"}");
            log.Record("write_file", "{\"path\":\"a\",\"content\":\"x\"}");
            log.Record("run_shell", "{\"command\":\"echo hi\"}");
            log.CommitTurn();

            Assert.True(log.HasReplay);
            Assert.Equal(2, log.LastCommitted.Count);
            Assert.Contains("write_file", log.Describe());
        }

        [Fact]
        public void GitBranchContext_ToPromptBlock_EmptyWhenNotRepo()
        {
            var snap = new GitBranchSnapshot(false, "", false, 0, "");
            Assert.Equal(string.Empty, GitBranchContext.ToPromptBlock(snap));
        }

        [Fact]
        public void GitBranchContext_ToPromptBlock_IncludesBranch()
        {
            var snap = new GitBranchSnapshot(true, "main", true, 2, " M foo.cs\n M bar.cs");
            string block = GitBranchContext.ToPromptBlock(snap);
            Assert.Contains("main", block);
            Assert.Contains("dirty: yes", block);
            Assert.Contains("GIT BRANCH CONTEXT", block);
        }
    }
}
