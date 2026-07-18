using System;
using System.IO;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ProjectMemoryTests
    {
        [Fact]
        public void BuildContextBlock_LoadsAxiomMd()
        {
            string root = Path.Combine(Path.GetTempPath(), "axiom-mem-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "AXIOM.md"), "# Rules\nUse spaces not tabs.\n");
            try
            {
                string block = ProjectMemory.BuildContextBlock(root);
                Assert.Contains("PROJECT MEMORY", block, StringComparison.Ordinal);
                Assert.Contains("Use spaces not tabs", block, StringComparison.Ordinal);
                Assert.Contains("AXIOM.md", block, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }
    }
}
