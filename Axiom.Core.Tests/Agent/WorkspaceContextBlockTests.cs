using System;
using System.IO;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class WorkspaceContextBlockTests
    {
        [Fact]
        public void BuildContextBlock_IncludesAccessGrantAndFileSample()
        {
            string root = Path.Combine(Path.GetTempPath(), "axiom-agent-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "readme.md"), "# hi");
            try
            {
                var ws = new WorkspaceSession(attachCwd: false);
                Assert.True(ws.TrySetExclusive(root));

                string block = ws.BuildContextBlock();
                Assert.Contains("YOU HAVE ACCESS", block, StringComparison.Ordinal);
                Assert.Contains(root, block, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("readme.md", block, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Never claim you lack access", block, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }
    }
}
