using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ApprovalGatingTests
    {
        // Regression guard: Auto mode is documented to the user as "write/shell freely in
        // sandbox" (see ChatTui /mode help) with no carve-out, but a large write_file used to
        // force an approval prompt anyway. Combined with a separate TUI bug where that prompt's
        // y/n keys could go unanswered forever, a big file write in Auto mode could hang the CLI
        // outright. Auto must never call the approval handler for write_file, regardless of size.
        [Fact]
        public async Task ExecuteAsync_AutoMode_NeverAsksApprovalForLargeWriteFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-approval-gating-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var workspace = new WorkspaceSession(attachCwd: false);
                Assert.True(workspace.TrySetExclusive(dir));

                var executor = new AgentToolExecutor(workspace) { ApprovalMode = ApprovalMode.Auto };
                bool approvalHandlerCalled = false;
                executor.ApprovalHandler = (_, _) =>
                {
                    approvalHandlerCalled = true;
                    return Task.FromResult(true);
                };

                var content = new StringBuilder();
                for (int i = 0; i < 200; i++)
                    content.AppendLine($"line {i}");

                var args = new
                {
                    path = "big.txt",
                    content = content.ToString()
                };
                string argsJson = JsonSerializer.Serialize(args);

                string result = await executor.ExecuteAsync("write_file", argsJson, CancellationToken.None);

                Assert.False(approvalHandlerCalled);
                Assert.DoesNotContain("Denied", result, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(Path.Combine(dir, "big.txt")));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public async Task ExecuteAsync_AskMode_StillAsksApprovalForWriteFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-approval-gating-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var workspace = new WorkspaceSession(attachCwd: false);
                Assert.True(workspace.TrySetExclusive(dir));

                var executor = new AgentToolExecutor(workspace) { ApprovalMode = ApprovalMode.Ask };
                bool approvalHandlerCalled = false;
                executor.ApprovalHandler = (_, _) =>
                {
                    approvalHandlerCalled = true;
                    return Task.FromResult(true);
                };

                var args = new { path = "small.txt", content = "hi" };
                string argsJson = JsonSerializer.Serialize(args);

                await executor.ExecuteAsync("write_file", argsJson, CancellationToken.None);

                Assert.True(approvalHandlerCalled);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }
    }
}
