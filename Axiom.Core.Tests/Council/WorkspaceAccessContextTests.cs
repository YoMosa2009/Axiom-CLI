using System;
using System.IO;
using Axiom.Core.Council;
using Axiom.Core.Workspace;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class WorkspaceAccessContextTests
    {
        [Fact]
        public void CreateFolderConnection_IndexesFiles_AndSetsFolderKind()
        {
            string root = Path.Combine(Path.GetTempPath(), "axiom-ws-ctx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Hello.cs"), "class Hello {}");
            try
            {
                var access = new WorkspaceAccessService();
                ConnectedWorkspaceState state = access.CreateFolderConnection(root);

                Assert.True(state.CodebaseEditAccessEnabled);
                Assert.Equal(WorkspaceConnectionKind.Folder.ToString(), state.ConnectionKind);
                Assert.True(state.IndexedFileCount >= 1);
                Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(state.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void BuildContextPacket_IncludesAccessGrantAndFileIndex()
        {
            string root = Path.Combine(Path.GetTempPath(), "axiom-ws-ctx2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "App.cs"), "namespace Demo { class App {} }");
            try
            {
                var access = new WorkspaceAccessService();
                ConnectedWorkspaceState state = access.CreateFolderConnection(root);
                WorkspaceContextResult packet = access.BuildContextPacket(state, "what is in App.cs", 20_000);

                Assert.Contains("YOU HAVE ACCESS", packet.Packet, StringComparison.Ordinal);
                Assert.Contains("FILE INDEX", packet.Packet, StringComparison.Ordinal);
                Assert.Contains("App.cs", packet.Packet, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("no readable local code files are connected yet", packet.Packet, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void BuildContextPacket_EmptyFolder_StillReportsConnectedAccess()
        {
            string root = Path.Combine(Path.GetTempPath(), "axiom-ws-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var access = new WorkspaceAccessService();
                ConnectedWorkspaceState state = access.CreateFolderConnection(root);
                WorkspaceContextResult packet = access.BuildContextPacket(state, "list files", 8_000);

                Assert.Contains("YOU HAVE ACCESS", packet.Packet, StringComparison.Ordinal);
                Assert.Contains(root, packet.Packet, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Ask the user to connect a local folder", packet.Packet, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Theory]
        [InlineData("what does this project do?", false)]
        [InlineData("list the files in the workspace", false)]
        [InlineData("explain Main.cs", false)]
        [InlineData("fix the null reference in Main.cs", true)]
        [InlineData("add a README and implement login", true)]
        [InlineData("refactor the builder module", true)]
        [InlineData("build a playable browser game", true)]
        [InlineData("make a .html artifact instead of an .exe", true)]
        [InlineData("generate a starter project with an API", true)]
        [InlineData("design a responsive landing page", true)]
        [InlineData("make sure the explanation is concise", false)]
        public void LooksLikeCodeEditRequest_ClassifiesIntents(string prompt, bool expectEdit)
        {
            Assert.Equal(expectEdit, CouncilOrchestrator.LooksLikeCodeEditRequest(prompt));
        }
    }
}
