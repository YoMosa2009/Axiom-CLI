using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ToolGatingHeuristicsTests
    {
        private static OpenRouterToolDefinition Tool(string name) => new(name, "desc", new JsonObject());

        private static readonly IReadOnlyList<OpenRouterToolDefinition> FullCatalog =
        [
            Tool("read_file"), Tool("list_dir"), Tool("search_files"), Tool("find_symbol"),
            Tool("read_csv"), Tool("read_notebook"),
            Tool("write_file"), Tool("str_replace"), Tool("apply_patch"), Tool("write_files"), Tool("plan_board"),
            Tool("run_shell"), Tool("diagnostics"), Tool("run_tests"), Tool("package_install"), Tool("docker_run"), Tool("run_background"),
            Tool("git_status"), Tool("git_commit"), Tool("worktree_create"), Tool("open_pr"),
            Tool("download_file"), Tool("fetch_url"), Tool("web_search"),
            Tool("spawn_subagent")
        ];

        [Fact]
        public void Filter_KeepsReadOnlyToolsForAPlainGreeting()
        {
            var result = ToolGatingHeuristics.Filter(FullCatalog, "hello", workspaceAttached: false);
            var names = result.Select(t => t.Name).ToHashSet();

            Assert.Contains("read_file", names);
            Assert.Contains("list_dir", names);
            Assert.Contains("search_files", names);
            Assert.Contains("find_symbol", names);
        }

        [Fact]
        public void Filter_ExcludesWriteAndShellToolsForAPlainGreeting()
        {
            var result = ToolGatingHeuristics.Filter(FullCatalog, "hello", workspaceAttached: false);
            var names = result.Select(t => t.Name).ToHashSet();

            Assert.DoesNotContain("write_file", names);
            Assert.DoesNotContain("str_replace", names);
            Assert.DoesNotContain("run_shell", names);
            Assert.DoesNotContain("git_commit", names);
            Assert.DoesNotContain("web_search", names);
            Assert.DoesNotContain("spawn_subagent", names);
        }

        [Fact]
        public void Filter_IncludesWriteToolsWhenWorkspaceIsAttached()
        {
            // The user pointing the agent at a folder is itself signal, even without explicit
            // edit-shaped wording in the message.
            var result = ToolGatingHeuristics.Filter(FullCatalog, "what should I add here?", workspaceAttached: true);
            var names = result.Select(t => t.Name).ToHashSet();

            Assert.Contains("write_file", names);
            Assert.Contains("str_replace", names);
        }

        [Fact]
        public void Filter_IncludesWriteToolsForExplicitEditRequest()
        {
            var result = ToolGatingHeuristics.Filter(FullCatalog, "fix the bug in Program.cs", workspaceAttached: false);
            var names = result.Select(t => t.Name).ToHashSet();

            Assert.Contains("write_file", names);
            Assert.Contains("str_replace", names);
        }

        [Fact]
        public void Filter_IncludesShellToolsForBuildOrRunRequest()
        {
            var result = ToolGatingHeuristics.Filter(FullCatalog, "run the tests and tell me if they pass", workspaceAttached: false);
            var names = result.Select(t => t.Name).ToHashSet();

            Assert.Contains("run_shell", names);
            Assert.Contains("run_tests", names);
        }

        [Fact]
        public void Filter_IncludesGitToolsOnlyForGitSignal()
        {
            var withoutGit = ToolGatingHeuristics.Filter(FullCatalog, "hello", workspaceAttached: false);
            var withGit = ToolGatingHeuristics.Filter(FullCatalog, "commit these changes to git", workspaceAttached: false);

            Assert.DoesNotContain(withoutGit, t => t.Name == "git_commit");
            Assert.Contains(withGit, t => t.Name == "git_commit");
        }

        [Fact]
        public void Filter_IncludesNetworkToolsOnlyForNetworkSignal()
        {
            var withoutNetwork = ToolGatingHeuristics.Filter(FullCatalog, "hello", workspaceAttached: false);
            var withNetwork = ToolGatingHeuristics.Filter(FullCatalog, "download https://example.com/file.zip", workspaceAttached: false);

            Assert.DoesNotContain(withoutNetwork, t => t.Name == "download_file");
            Assert.Contains(withNetwork, t => t.Name == "download_file");
        }

        [Fact]
        public void Filter_KeepsUnknownFutureToolsByDefault()
        {
            var tools = new List<OpenRouterToolDefinition> { Tool("some_new_tool_added_later") };
            var result = ToolGatingHeuristics.Filter(tools, "hello", workspaceAttached: false);

            Assert.Contains(result, t => t.Name == "some_new_tool_added_later");
        }
    }
}
