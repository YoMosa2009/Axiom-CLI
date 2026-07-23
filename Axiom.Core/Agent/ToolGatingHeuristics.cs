using System;
using System.Collections.Generic;
using System.Linq;
using Axiom.Core.Chat;
using Axiom.Core.Council;

namespace Axiom.Core.Agent
{
    // Keeps a small self-hosted model's tool menu proportional to what the message plausibly
    // needs, instead of exposing the same ~20-tool catalog for "hello" as for "build me an app".
    // Mirrors the heuristic-gating pattern already proven in the sibling desktop app (Malx_AI
    // MainWindow.Cloud.cs's LooksLikeCalculationRequest/LooksLikeCodeExecutionRequest), re-derived
    // here for Axiom-CLI's actual tool set. Only ever invoked when explicitly asked to (the custom
    // endpoint path) -- eidos/hepha never call this, so their tool exposure is unchanged.
    public static class ToolGatingHeuristics
    {
        private static readonly string[] BuildRunSignalWords =
        [
            "run", "build", "compile", "execute", "test", "npm", "dotnet", "pytest", "cargo",
            "install", "package", "docker", "container", "script"
        ];

        private static readonly string[] GitSignalWords =
        [
            "git", "commit", "branch", "checkout", "worktree", "pull request", " pr ", "push"
        ];

        private static readonly string[] NetworkSignalWords =
        [
            "http://", "https://", "download", "fetch", "url", "web search", "search the web",
            "look up online", "internet"
        ];

        private static readonly string[] SubagentSignalWords =
        [
            "subagent", "background task", "explore agent", "in parallel"
        ];

        private static readonly HashSet<string> AlwaysKeep = new(StringComparer.OrdinalIgnoreCase)
        {
            "read_file", "list_dir", "search_files", "find_symbol", "read_csv", "read_notebook"
        };

        private static readonly HashSet<string> EditGatedTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "str_replace", "apply_patch", "write_files", "plan_board"
        };

        private static readonly HashSet<string> BuildRunGatedTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "run_shell", "diagnostics", "run_tests", "package_install", "docker_run", "run_background"
        };

        private static readonly HashSet<string> GitGatedTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "git_status", "git_diff", "git_log", "git_branch", "git_commit", "git_checkout",
            "worktree_create", "worktree_list", "worktree_remove", "open_pr"
        };

        private static readonly HashSet<string> NetworkGatedTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "download_file", "fetch_url", "web_search"
        };

        private static readonly HashSet<string> SubagentGatedTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "spawn_subagent"
        };

        public static bool LooksLikeBuildOrRunTask(string message) => ContainsAny(message, BuildRunSignalWords);
        public static bool LooksLikeGitTask(string message) => ContainsAny(message, GitSignalWords);
        public static bool LooksLikeNetworkTask(string message) => ContainsAny(message, NetworkSignalWords);
        public static bool LooksLikeSubagentTask(string message) => ContainsAny(message, SubagentSignalWords);

        private static bool ContainsAny(string message, string[] signals)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;
            string lower = " " + message.ToLowerInvariant() + " ";
            return signals.Any(lower.Contains);
        }

        /// <summary>
        /// Filters a full tool list down to what a message plausibly needs. Read-only tools
        /// (read_file/list_dir/search_files/find_symbol/read_csv/read_notebook) always stay --
        /// a small model should always be able to look before acting. Everything else is gated
        /// on a purpose-built heuristic per category, with an attached workspace alone treated as
        /// enough signal to allow writes (the user pointing the agent at a folder is itself intent).
        /// </summary>
        public static IReadOnlyList<OpenRouterToolDefinition> Filter(
            IReadOnlyList<OpenRouterToolDefinition> tools,
            string? userMessage,
            bool workspaceAttached)
        {
            string message = userMessage ?? string.Empty;
            bool looksLikeEdit = CouncilOrchestrator.LooksLikeCodeEditRequest(message) || workspaceAttached;
            bool looksLikeBuildOrRun = LooksLikeBuildOrRunTask(message);
            bool looksLikeGit = LooksLikeGitTask(message);
            bool looksLikeNetwork = LooksLikeNetworkTask(message);
            bool looksLikeSubagent = LooksLikeSubagentTask(message);

            return tools.Where(t => ShouldKeep(t.Name, looksLikeEdit, looksLikeBuildOrRun, looksLikeGit, looksLikeNetwork, looksLikeSubagent)).ToList();
        }

        private static bool ShouldKeep(
            string name,
            bool looksLikeEdit,
            bool looksLikeBuildOrRun,
            bool looksLikeGit,
            bool looksLikeNetwork,
            bool looksLikeSubagent)
        {
            if (AlwaysKeep.Contains(name)) return true;
            if (EditGatedTools.Contains(name)) return looksLikeEdit || looksLikeBuildOrRun;
            if (BuildRunGatedTools.Contains(name)) return looksLikeBuildOrRun || looksLikeEdit;
            if (GitGatedTools.Contains(name)) return looksLikeGit;
            if (NetworkGatedTools.Contains(name)) return looksLikeNetwork;
            if (SubagentGatedTools.Contains(name)) return looksLikeSubagent;
            // Unknown/future tool: default to keeping it rather than silently hiding new tools
            // from this gate whenever the tool catalog grows.
            return true;
        }
    }
}
