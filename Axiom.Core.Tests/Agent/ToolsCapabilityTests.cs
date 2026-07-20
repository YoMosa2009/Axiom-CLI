using System;
using System.IO;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ToolsCapabilityTests
    {
        [Fact]
        public void SecretRedaction_RedactsApiKeysAndBearer()
        {
            string raw = "key=sk-abcdefghijklmnopqrstuvwxyz012345 Authorization: Bearer tokensecretvalue123";
            string red = SecretRedaction.Redact(raw);
            Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz012345", red);
            Assert.Contains("[REDACTED]", red);
            Assert.Contains("Authorization:", red);
        }

        [Fact]
        public void ShellPolicy_BuiltinDeny_BlocksForcePush()
        {
            var policy = new ShellPolicy();
            // Use Load defaults via empty root
            policy = ShellPolicy.Load(Path.GetTempPath());
            Assert.False(policy.TryAuthorize("git push --force origin main", out string reason));
            Assert.Contains("denied", reason, StringComparison.OrdinalIgnoreCase);
            Assert.True(policy.TryAuthorize("dotnet test", out _));
        }

        [Fact]
        public void StrReplace_ReplacesExactMatch()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-sr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "hello world\n");
            try
            {
                string r = ApplyPatchService.StrReplace(path, "world", "axiom");
                Assert.DoesNotContain("Error", r, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("hello axiom\n", File.ReadAllText(path));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ApplyPatch_UpdateFile_Hunk()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-ap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "b.txt");
            File.WriteAllText(path, "line1\nline2\nline3\n");
            try
            {
                string patch =
                    "*** Begin Patch\n" +
                    "*** Update File: b.txt\n" +
                    "@@\n" +
                    "-line2\n" +
                    "+line2-fixed\n" +
                    "*** End Patch\n";
                string result = ApplyPatchService.ApplyStructuredPatch(dir, patch, rel => Path.Combine(dir, rel));
                Assert.Contains("updated", result, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("line2-fixed", File.ReadAllText(path));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void DataFileTools_ReadCsv_ShowsRows()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-csv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "t.csv");
            File.WriteAllText(path, "a,b\n1,2\n3,4\n");
            try
            {
                string r = DataFileTools.ReadCsv(path, maxRows: 10);
                Assert.Contains("1 | 2", r);
                Assert.Contains("rows_shown", r);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void NetworkPolicy_Offline_Blocks()
        {
            var n = new NetworkPolicy { Offline = true };
            Assert.NotNull(n.Block("fetch_url"));
            n.Offline = false;
            Assert.Null(n.Block("fetch_url"));
        }

        [Fact]
        public void IsParallelSafeTool_ReadsOnly()
        {
            Assert.True(AgentToolExecutor.IsParallelSafeTool("read_file"));
            Assert.True(AgentToolExecutor.IsParallelSafeTool("search_files"));
            Assert.False(AgentToolExecutor.IsParallelSafeTool("write_file"));
            Assert.False(AgentToolExecutor.IsParallelSafeTool("run_shell"));
        }
    }
}
