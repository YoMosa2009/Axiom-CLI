using System;
using System.IO;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class WorkspaceSessionTests
    {
        [Fact]
        public void ExclusiveLock_RejectsPathsOutsideRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "axiom-ws-" + Guid.NewGuid().ToString("N"));
            string outside = Path.Combine(Path.GetTempPath(), "axiom-out-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            try
            {
                var ws = new WorkspaceSession(attachCwd: false);
                Assert.True(ws.TrySetExclusive(root));
                Assert.True(ws.IsExclusive);
                Assert.True(ws.IsPathAllowed(root));
                Assert.True(ws.IsPathAllowed(Path.Combine(root, "sub", "file.txt")));
                Assert.False(ws.IsPathAllowed(outside));
                Assert.False(ws.IsPathAllowed(Path.Combine(outside, "x.txt")));

                // Parent traversal must not escape.
                string escaped = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(outside)));
                if (Directory.Exists(escaped) || File.Exists(escaped) || true)
                    Assert.False(ws.IsPathAllowed(Path.Combine(root, "..", Path.GetFileName(outside), "x.txt")));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
                try { Directory.Delete(outside, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ShellValidation_BlocksAbsoluteOutsidePaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "axiom-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var ws = new WorkspaceSession(attachCwd: false);
                Assert.True(ws.TrySetExclusive(root));

                Assert.True(ws.TryValidateShellCommand("echo hi", root, out _));
                Assert.True(ws.TryValidateShellCommand("ls .", root, out _));

                string outside = Path.GetFullPath(Path.Combine(root, "..", "not-in-sandbox-" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(outside);
                try
                {
                    Assert.False(ws.TryValidateShellCommand($"cat {outside}/secret.txt", root, out string reason));
                    Assert.Contains("outside", reason, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    try { Directory.Delete(outside, true); } catch { /* ignore */ }
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void IsUnderRoot_RespectsPlatformPathRules()
        {
            string root = Path.Combine(Path.GetTempPath(), "AxiomCase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string child = Path.Combine(root, "Child");
                Directory.CreateDirectory(child);

                Assert.True(WorkspaceSession.IsUnderRoot(child, root));
                Assert.True(WorkspaceSession.IsUnderRoot(root, root));

                if (OperatingSystem.IsLinux())
                {
                    // On Linux, a different-case sibling path is a different path.
                    string weird = root.ToUpperInvariant();
                    if (!string.Equals(weird, root, StringComparison.Ordinal))
                        Assert.False(WorkspaceSession.IsUnderRoot(Path.Combine(weird, "x"), root));
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }
    }
}
