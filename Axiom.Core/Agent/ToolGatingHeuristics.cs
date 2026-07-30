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
            "read_file", "list_dir", "search_files", "find_symbol", "read_csv", "read_notebook", "calculator",
            "device_info", "list_serial_ports"
        };

        private static readonly HashSet<string> EditGatedTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "str_replace", "apply_patch", "write_files", "plan_board"
        };

        // Near-universally useful for any real coding task -- verifying/executing/testing code is
        // something almost every edit-shaped request eventually needs, regardless of whether the
        // message said "run". Kept unconditional for edit-shaped tasks below.
        private static readonly HashSet<string> CoreExecutionTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "run_shell", "diagnostics", "run_tests", "run_background", "ide_open"
        };

        // Tools that install/pull software or otherwise add new things to the machine, as opposed
        // to running/verifying what's already there. Unlike CoreExecutionTools these stay behind an
        // explicit build/run/install signal even for edit-shaped tasks: a small model reaches for a
        // purpose-built "package_install" tool sitting right there in the menu far more readily than
        // it would type the equivalent `npm install` into run_shell, so surfacing it unconditionally
        // made Kestral install things nobody asked for. A task that genuinely needs a dependency
        // without saying so explicitly is still covered -- run_shell (always available above) plus
        // the [SELF-SUFFICIENT SETUP] system-prompt instruction handle that case.
        private static readonly HashSet<string> EnvironmentMutatingTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "package_install", "docker_run"
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

        // Deliberately narrow (exact match after trimming trailing punctuation): only messages
        // that are plausibly nothing but a greeting/ack. A short message alone isn't enough
        // signal -- "fix main.cs" is three words and a real task -- so this must never fuzzy-match
        // a prefix of a longer request.
        private static readonly HashSet<string> SmallTalkPhrases = new(StringComparer.OrdinalIgnoreCase)
        {
            "hi", "hey", "hello", "hiya", "yo", "sup", "howdy",
            "hi there", "hey there", "hello there",
            "thanks", "thank you", "thx", "ty",
            "ok", "okay", "cool", "nice", "great", "got it", "sounds good",
            "bye", "goodbye", "see you", "good morning", "good afternoon", "good evening",
            "how are you", "how's it going", "hows it going", "what's up", "whats up",
            "who are you", "what can you do"
        };

        // Small self-hosted models have substantially better call accuracy when they see a
        // focused coding surface instead of every possible Git/network/worktree/package
        // operation. Keep core inspection, every normal file-edit primitive, and verification
        // together: hiding batch writes or patches made multi-file tasks needlessly turn into
        // fragile repeated calls. Kestral 1 is currently omnicoder-2-9b (previously granite3.2:8b).
        private static readonly HashSet<string> CompactEditTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "read_file", "list_dir", "search_files", "find_symbol", "calculator",
            "write_file", "str_replace", "apply_patch", "write_files",
            "diagnostics", "run_tests"
        };

        public static bool LooksLikeBuildOrRunTask(string message) => ContainsAny(message, BuildRunSignalWords);
        public static bool LooksLikeGitTask(string message) => ContainsAny(message, GitSignalWords);
        public static bool LooksLikeNetworkTask(string message) => ContainsAny(message, NetworkSignalWords);
        public static bool LooksLikeSubagentTask(string message) => ContainsAny(message, SubagentSignalWords);

        public static bool LooksLikeSmallTalk(string? message)
        {
            string trimmed = (message ?? string.Empty).Trim().TrimEnd('!', '.', '?', ',');
            return trimmed.Length > 0 && SmallTalkPhrases.Contains(trimmed);
        }

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

            // A bare greeting/ack has no plausible tool need. Previously an attached workspace
            // alone was enough to pull in the full write/edit tool belt (10+ JSON schemas) for
            // *any* message, including "hello" -- on a self-hosted small model that is pure added
            // prompt size plus pressure (from the system prompt's "always act, never disclaim"
            // instruction) toward a spurious tool-call attempt for a turn that needs none. Checked
            // before the workspace-attached broadening below so it still wins even with a folder
            // locked.
            if (LooksLikeSmallTalk(message))
                return tools.Where(t => ShouldKeep(t.Name, false, false, false, false, false)).ToList();

            bool looksLikeEdit = CouncilOrchestrator.LooksLikeCodeEditRequest(message) || workspaceAttached;
            bool looksLikeBuildOrRun = LooksLikeBuildOrRunTask(message);
            bool looksLikeGit = LooksLikeGitTask(message);
            bool looksLikeNetwork = LooksLikeNetworkTask(message);
            bool looksLikeSubagent = LooksLikeSubagentTask(message);

            if (looksLikeEdit)
            {
                // Start small for call accuracy, but do not let an edit-shaped message suppress
                // capabilities explicitly requested by the user (for example, build/test/shell).
                // The old early return made "build this app and run the tests" lose run_shell
                // merely because it was also an edit request.
                //
                // CoreExecutionTools is unconditional here (run/verify is near-universal for any
                // real coding task), but EnvironmentMutatingTools (package_install/docker_run)
                // still requires an explicit build/run/install signal -- see its own comment for
                // why installs specifically need a real trigger, not just "a workspace is attached".
                var allowed = new HashSet<string>(CompactEditTools, StringComparer.OrdinalIgnoreCase);
                allowed.UnionWith(CoreExecutionTools);
                AddIf(allowed, EnvironmentMutatingTools, looksLikeBuildOrRun);
                AddIf(allowed, GitGatedTools, looksLikeGit);
                AddIf(allowed, NetworkGatedTools, looksLikeNetwork);
                AddIf(allowed, SubagentGatedTools, looksLikeSubagent);
                return tools.Where(t => allowed.Contains(t.Name)).ToList();
            }

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
            if (CoreExecutionTools.Contains(name)) return looksLikeBuildOrRun || looksLikeEdit;
            if (EnvironmentMutatingTools.Contains(name)) return looksLikeBuildOrRun;
            if (GitGatedTools.Contains(name)) return looksLikeGit;
            if (NetworkGatedTools.Contains(name)) return looksLikeNetwork;
            if (SubagentGatedTools.Contains(name)) return looksLikeSubagent;
            // Unknown/future tool: default to keeping it rather than silently hiding new tools
            // from this gate whenever the tool catalog grows.
            return true;
        }

        private static void AddIf(HashSet<string> target, IEnumerable<string> source, bool condition)
        {
            if (!condition)
                return;

            target.UnionWith(source);
        }
    }
}
