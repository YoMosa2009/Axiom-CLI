using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Axiom.Core.Tools;
using HtmlAgilityPack;

namespace Axiom.Core.Agent
{
    // Executes agent tool calls (shell, files, download, web search) inside the user-attached workspaces.
    public sealed class AgentToolExecutor
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private const int MaxOutputChars = 48_000;
        private const int DefaultShellTimeoutSeconds = 120;

        private readonly WorkspaceSession _workspace;
        private readonly WebSearchService _webSearch = new();
        private readonly List<string> _writtenPaths = new();
        private readonly object _writeGate = new();
        private readonly FileChangeUndo _undo = new();
        private readonly SessionWorkflowState _workflow = new();
        private readonly NetworkPolicy _network = new();
        private ShellPolicy? _shellPolicy;

        public AgentToolExecutor(WorkspaceSession workspace)
        {
            _workspace = workspace;
        }

        /// <summary>When false, web_search is omitted from the tool list and rejected if called.</summary>
        public bool WebSearchEnabled { get; set; } = true;

        public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Auto;

        /// <summary>Optional host prompt for Ask mode (write/shell/download).</summary>
        public ToolApprovalHandler? ApprovalHandler { get; set; }

        /// <summary>Optional subagent factory for spawn_subagent tool.</summary>
        public Func<SubagentKind, string, CancellationToken, Task<SubagentResult>>? SubagentHandler { get; set; }

        public FileChangeUndo Undo => _undo;
        public SessionWorkflowState Workflow => _workflow;
        public NetworkPolicy Network => _network;

        private ShellPolicy ShellPolicyCached
            => _shellPolicy ??= ShellPolicy.Load(_workspace.PrimaryRoot);

        public void ReloadPolicies() => _shellPolicy = null;

        /// <summary>Absolute paths successfully written via write_file since last <see cref="ClearWrittenPaths"/>.</summary>
        public IReadOnlyList<string> WrittenPaths
        {
            get { lock (_writeGate) return _writtenPaths.ToList(); }
        }

        public void ClearWrittenPaths()
        {
            lock (_writeGate) _writtenPaths.Clear();
        }

        public void BeginUndoTurn(string label)
        {
            _undo.BeginTurn(label);
            _workflow.Replay.BeginTurn();
            _workflow.Changes.BeginTurn();
        }

        public void CommitUndoTurn()
        {
            _undo.CommitTurn();
            _workflow.Replay.CommitTurn();
        }

        public enum ToolScope
        {
            /// <summary>Full coding agent surface (Builder / solo agent).</summary>
            Full,
            /// <summary>Read/search/test only — Critic falsification pass (desktop-style).</summary>
            Inspect
        }

        public IReadOnlyList<OpenRouterToolDefinition> GetToolDefinitions(ToolScope scope = ToolScope.Full)
        {
            var tools = new List<OpenRouterToolDefinition>
            {
                new(
                    "run_shell",
                    scope == ToolScope.Inspect
                        ? "Run a non-destructive shell command to verify work (tests, build, git status). Do not write files or install packages."
                        : "Run a shell command in the workspace (build, install, git, scripts, tests, etc.).",
                    Schema(new JsonObject
                    {
                        ["command"] = Prop("string", "Shell command to execute"),
                        ["working_directory"] = Prop("string", "Optional cwd relative to or under an attached workspace"),
                        ["timeout_seconds"] = Prop("integer", "Optional timeout (default 120, max 300)")
                    }, required: ["command"])),

                new(
                    "read_file",
                    "Read a text file from an attached workspace (supports offset/limit pagination).",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "File path relative to primary workspace or absolute under an attached root"),
                        ["offset"] = Prop("integer", "1-based start line (optional)"),
                        ["limit"] = Prop("integer", "Max lines to return (optional, default all/capped)")
                    }, required: ["path"])),

                new(
                    "list_dir",
                    "List files and folders in a directory under an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "Directory path (default: primary workspace root)"),
                        ["recursive"] = Prop("boolean", "If true, list nested entries (capped)")
                    }, required: [])),

                new(
                    "search_files",
                    "Ripgrep-class search across the workspace (uses rg when installed). Returns path:line:snippet.",
                    Schema(new JsonObject
                    {
                        ["query"] = Prop("string", "Text or regex to search for"),
                        ["path"] = Prop("string", "Directory to search (default: primary workspace)"),
                        ["glob"] = Prop("string", "Optional file filter e.g. *.cs"),
                        ["regex"] = Prop("boolean", "If true, treat query as regex (default false)"),
                        ["max_hits"] = Prop("integer", "Max matches (default 80, max 200)"),
                        ["offset"] = Prop("integer", "Skip first N matches (pagination cursor)")
                    }, required: ["query"])),

                new(
                    "find_symbol",
                    "Find definitions or references of a symbol (lightweight LSP-style via search).",
                    Schema(new JsonObject
                    {
                        ["symbol"] = Prop("string", "Symbol name"),
                        ["mode"] = Prop("string", "def | refs (default def)")
                    }, required: ["symbol"])),

                new(
                    "git_status",
                    "Show git status --short --branch for the workspace root.",
                    Schema(new JsonObject(), required: [])),

                new(
                    "git_diff",
                    "Show git diff (optionally staged).",
                    Schema(new JsonObject
                    {
                        ["staged"] = Prop("boolean", "If true, show staged diff only")
                    }, required: [])),

                new(
                    "git_log",
                    "Show recent commits (oneline).",
                    Schema(new JsonObject
                    {
                        ["count"] = Prop("integer", "How many commits (default 12)")
                    }, required: [])),

                new(
                    "git_branch",
                    "List local branches (-vv).",
                    Schema(new JsonObject(), required: [])),

                new(
                    "diagnostics",
                    "Detect project type and run build/test diagnostics (dotnet/npm/pytest/cargo/go).",
                    Schema(new JsonObject
                    {
                        ["prefer"] = Prop("string", "Optional: dotnet|node|python|rust|go")
                    }, required: [])),

                new(
                    "run_tests",
                    "Run project tests with optional filter (failed-only re-run when supported).",
                    Schema(new JsonObject
                    {
                        ["filter"] = Prop("string", "Test name filter (dotnet --filter / pytest -k / go -run)"),
                        ["prefer"] = Prop("string", "Optional: dotnet|node|python|rust|go"),
                        ["coverage"] = Prop("boolean", "If true, attempt coverage summary when tool available")
                    }, required: [])),

                new(
                    "read_csv",
                    "Preview a CSV/TSV file as a small table.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "CSV path"),
                        ["max_rows"] = Prop("integer", "Rows to show (default 30)")
                    }, required: ["path"])),

                new(
                    "read_notebook",
                    "Summarize a Jupyter .ipynb notebook (cell types + source preview).",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "Notebook path"),
                        ["max_cells"] = Prop("integer", "Cells to show (default 20)")
                    }, required: ["path"]))
            };

            if (scope == ToolScope.Full)
            {
                tools.Insert(2, new(
                    "write_file",
                    "Create or overwrite a text file inside an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "Target file path"),
                        ["content"] = Prop("string", "Full file contents to write")
                    }, required: ["path", "content"])));

                tools.Insert(3, new(
                    "str_replace",
                    "Exact search-and-replace in a file (preferred for small edits).",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "File path"),
                        ["old_string"] = Prop("string", "Exact text to find"),
                        ["new_string"] = Prop("string", "Replacement text"),
                        ["replace_all"] = Prop("boolean", "Replace every match (default false)")
                    }, required: ["path", "old_string", "new_string"])));

                tools.Insert(4, new(
                    "apply_patch",
                    "Apply a structured multi-file patch (*** Update/Add/Delete File: … with -/+ lines).",
                    Schema(new JsonObject
                    {
                        ["patch"] = Prop("string", "Patch text")
                    }, required: ["patch"])));

                tools.Insert(5, new(
                    "write_files",
                    "Atomic multi-file write in one undo unit (transaction).",
                    Schema(new JsonObject
                    {
                        ["files"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Array of {path, content} objects",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = Prop("string", "File path"),
                                    ["content"] = Prop("string", "Full contents")
                                }
                            }
                        }
                    }, required: ["files"])));

                tools.Add(new(
                    "download_file",
                    "Download a URL into a path under an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["url"] = Prop("string", "HTTP/HTTPS URL"),
                        ["path"] = Prop("string", "Destination file path inside the workspace")
                    }, required: ["url", "path"])));

                tools.Add(new(
                    "fetch_url",
                    "Fetch a URL and return readable text/markdown (docs pages, raw files).",
                    Schema(new JsonObject
                    {
                        ["url"] = Prop("string", "HTTP/HTTPS URL"),
                        ["max_chars"] = Prop("integer", "Max characters to return (default 12000)")
                    }, required: ["url"])));

                tools.Add(new(
                    "package_install",
                    "Install a package with confirmation (dotnet add / npm install / pip install).",
                    Schema(new JsonObject
                    {
                        ["ecosystem"] = Prop("string", "dotnet | npm | pip"),
                        ["package"] = Prop("string", "Package name (and optional version)"),
                        ["project"] = Prop("string", "Optional project path for dotnet add")
                    }, required: ["ecosystem", "package"])));

                tools.Add(new(
                    "docker_run",
                    "Run a command in Docker (uses Dockerfile/devcontainer if present).",
                    Schema(new JsonObject
                    {
                        ["command"] = Prop("string", "Command to run inside container"),
                        ["image"] = Prop("string", "Optional image override")
                    }, required: ["command"])));

                tools.Add(new(
                    "git_commit",
                    "Stage all changes and create a git commit with the given message.",
                    Schema(new JsonObject
                    {
                        ["message"] = Prop("string", "Commit message")
                    }, required: ["message"])));

                tools.Add(new(
                    "git_checkout",
                    "Checkout an existing branch or create a new one.",
                    Schema(new JsonObject
                    {
                        ["branch"] = Prop("string", "Branch name"),
                        ["create"] = Prop("boolean", "If true, create with checkout -b")
                    }, required: ["branch"])));

                tools.Add(new(
                    "worktree_create",
                    "Create an isolated git worktree + branch for agent work (does not merge).",
                    Schema(new JsonObject
                    {
                        ["branch"] = Prop("string", "Optional branch name (default axiom/timestamp)")
                    }, required: [])));

                tools.Add(new(
                    "worktree_list",
                    "List git worktrees for the repo.",
                    Schema(new JsonObject(), required: [])));

                tools.Add(new(
                    "worktree_remove",
                    "Remove a git worktree by path.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "Worktree path from worktree_list")
                    }, required: ["path"])));

                tools.Add(new(
                    "spawn_subagent",
                    "Run a bounded specialist subagent: explore (read-only), tests, or fix.",
                    Schema(new JsonObject
                    {
                        ["kind"] = Prop("string", "explore | tests | fix"),
                        ["task"] = Prop("string", "What the subagent should do")
                    }, required: ["kind", "task"])));

                tools.Add(new(
                    "plan_board",
                    "Update the multi-step plan board (list / done / doing / skip / set from text).",
                    Schema(new JsonObject
                    {
                        ["action"] = Prop("string", "list | done | doing | skip | clear | set"),
                        ["step"] = Prop("integer", "1-based step index for done/doing/skip"),
                        ["text"] = Prop("string", "For action=set: numbered plan text to load")
                    }, required: ["action"])));

                tools.Add(new(
                    "run_background",
                    "Start a long shell command in the background (user can keep chatting; check with /jobs).",
                    Schema(new JsonObject
                    {
                        ["command"] = Prop("string", "Shell command to run asynchronously")
                    }, required: ["command"])));

                tools.Add(new(
                    "open_pr",
                    "Push current branch and open a GitHub PR via gh (title + body).",
                    Schema(new JsonObject
                    {
                        ["title"] = Prop("string", "PR title"),
                        ["body"] = Prop("string", "PR body markdown")
                    }, required: ["title"])));

                if (WebSearchEnabled)
                {
                    tools.Add(new(
                        "web_search",
                        "Search the live web for current information, docs, news, or facts. Use when the answer needs up-to-date external data.",
                        Schema(new JsonObject
                        {
                            ["query"] = Prop("string", "Search query in natural language")
                        }, required: ["query"])));
                }
            }

            return tools;
        }

        public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken token, ToolScope scope = ToolScope.Full)
        {
            if (scope == ToolScope.Inspect
                && toolName is "write_file" or "str_replace" or "apply_patch" or "write_files"
                    or "download_file" or "fetch_url" or "web_search"
                    or "git_commit" or "git_checkout" or "worktree_create" or "worktree_remove"
                    or "spawn_subagent" or "run_background" or "open_pr"
                    or "package_install" or "docker_run")
            {
                return $"Error: tool '{toolName}' is not available in Critic inspect mode.";
            }

            return await ExecuteAsync(toolName, argumentsJson, token);
        }

        public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken token)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                JsonElement root = doc.RootElement;
                string name = toolName ?? string.Empty;

                if (_network.IsNetworkTool(name))
                {
                    string? blocked = _network.Block(name);
                    if (blocked != null)
                        return blocked;
                }

                // Approval / plan gates for mutating tools.
                if (IsMutatingTool(name) || (_network.RequireApproval && _network.IsNetworkTool(name)))
                {
                    string summary = BuildApprovalSummary(name, root);
                    if (ApprovalMode == ApprovalMode.Plan && IsMutatingTool(name))
                        return $"Plan-only: would run {name} — {summary}";

                    bool forceAsk = ApprovalMode == ApprovalMode.Ask
                        || (ApprovalMode == ApprovalMode.Auto && name == "write_file" && IsBigWrite(root))
                        || (ApprovalMode == ApprovalMode.Auto && name is "package_install" or "docker_run")
                        || (_network.RequireApproval && _network.IsNetworkTool(name) && ApprovalMode != ApprovalMode.Plan);

                    if (forceAsk && ApprovalHandler != null)
                    {
                        string detail = name is "write_file" or "write_files" or "apply_patch"
                            ? summary + " (review recommended)"
                            : summary;
                        bool ok = await ApprovalHandler(
                            new ToolApprovalRequest(name, detail, argumentsJson ?? "{}"),
                            token);
                        if (!ok)
                            return $"Denied: user rejected {name} ({summary})";
                    }
                }

                _workflow.Replay.Record(name, argumentsJson ?? "{}");

                string raw = name switch
                {
                    "run_shell" => await RunShellAsync(root, token),
                    "read_file" => ReadFile(root),
                    "write_file" => WriteFile(root),
                    "str_replace" => StrReplace(root),
                    "apply_patch" => ApplyPatch(root),
                    "write_files" => WriteFiles(root),
                    "list_dir" => ListDir(root),
                    "download_file" => await DownloadAsync(root, token),
                    "fetch_url" => await FetchUrlAsync(root, token),
                    "search_files" => await SearchFilesAsync(root, token),
                    "find_symbol" => await SymbolSearchService.FindAsync(
                        _workspace.PrimaryRoot, GetString(root, "symbol"), GetString(root, "mode"), token),
                    "web_search" => await WebSearchAsync(root, token),
                    "git_status" => await GitWorkspaceService.StatusAsync(_workspace.PrimaryRoot, token),
                    "git_diff" => await GitWorkspaceService.DiffAsync(
                        _workspace.PrimaryRoot, token,
                        staged: root.TryGetProperty("staged", out JsonElement st) && st.ValueKind == JsonValueKind.True),
                    "git_log" => await GitWorkspaceService.LogAsync(
                        _workspace.PrimaryRoot, token,
                        count: root.TryGetProperty("count", out JsonElement c) && c.TryGetInt32(out int n) ? n : 12),
                    "git_branch" => await GitWorkspaceService.BranchAsync(_workspace.PrimaryRoot, token),
                    "git_commit" => await GitWorkspaceService.CommitAsync(
                        _workspace.PrimaryRoot, GetString(root, "message"), token),
                    "git_checkout" => await GitWorkspaceService.CheckoutBranchAsync(
                        _workspace.PrimaryRoot,
                        GetString(root, "branch"),
                        create: root.TryGetProperty("create", out JsonElement cr) && cr.ValueKind == JsonValueKind.True,
                        token),
                    "diagnostics" => await DiagnosticsService.RunAsync(
                        _workspace.PrimaryRoot, token, GetString(root, "prefer")),
                    "run_tests" => await RunTestsTrackedAsync(root, token),
                    "read_csv" => ReadCsvTool(root),
                    "read_notebook" => ReadNotebookTool(root),
                    "package_install" => await PackageInstallAsync(root, token),
                    "docker_run" => await DockerRunAsync(root, token),
                    "worktree_create" => await GitWorktreeService.CreateAsync(
                        _workspace.PrimaryRoot, GetString(root, "branch"), token),
                    "worktree_list" => await GitWorktreeService.ListAsync(_workspace.PrimaryRoot, token),
                    "worktree_remove" => await GitWorktreeService.RemoveAsync(
                        _workspace.PrimaryRoot, GetString(root, "path"), token),
                    "spawn_subagent" => await SpawnSubagentAsync(root, token),
                    "plan_board" => PlanBoardAction(root),
                    "run_background" => _workflow.Jobs.Start(GetString(root, "command"), _workspace.PrimaryRoot),
                    "open_pr" => await GitBranchContext.CreatePullRequestAsync(
                        _workspace.PrimaryRoot,
                        GetString(root, "title"),
                        GetString(root, "body"),
                        token),
                    _ => $"Unknown tool: {toolName}"
                };

                return SecretRedaction.Redact(raw);
            }
            catch (OperationCanceledException)
            {
                return "Error: cancelled.";
            }
            catch (Exception ex)
            {
                return $"Tool error ({toolName}): {ex.Message}";
            }
        }

        private static bool IsMutatingTool(string name) => name is
            "write_file" or "str_replace" or "apply_patch" or "write_files"
            or "run_shell" or "download_file" or "git_commit" or "git_checkout"
            or "worktree_create" or "worktree_remove" or "spawn_subagent" or "run_background" or "open_pr"
            or "package_install" or "docker_run";

        /// <summary>Tools that are safe to run concurrently when the model batches them.</summary>
        public static bool IsParallelSafeTool(string name) => name is
            "read_file" or "list_dir" or "search_files" or "find_symbol"
            or "git_status" or "git_diff" or "git_log" or "git_branch"
            or "read_csv" or "read_notebook" or "web_search" or "fetch_url";

        private bool IsBigWrite(JsonElement root)
        {
            string content = GetString(root, "content");
            int lines = content.Split('\n').Length;
            if (lines >= _workflow.BigDiffLineThreshold)
                return true;
            // Also treat as big if this turn already touched many files
            return _workflow.Changes.Files.Count + 1 >= _workflow.BigDiffFileThreshold;
        }

        private string PlanBoardAction(JsonElement root)
        {
            string action = GetString(root, "action").Trim().ToLowerInvariant();
            int step = root.TryGetProperty("step", out JsonElement s) && s.TryGetInt32(out int n) ? n : 0;
            switch (action)
            {
                case "list":
                case "":
                    return _workflow.Plan.ToDisplayBlock();
                case "clear":
                    _workflow.Plan.Clear();
                    return "Plan board cleared.";
                case "set":
                    _workflow.Plan.SetFromArchitectPlan(GetString(root, "text"));
                    return _workflow.Plan.ToDisplayBlock();
                case "done":
                    return _workflow.Plan.TryMarkDone(step)
                        ? _workflow.Plan.ToDisplayBlock()
                        : $"Error: step {step} not found.";
                case "doing":
                    return _workflow.Plan.TrySetStatus(step, PlanStepStatus.Doing)
                        ? _workflow.Plan.ToDisplayBlock()
                        : $"Error: step {step} not found.";
                case "skip":
                    return _workflow.Plan.TrySetStatus(step, PlanStepStatus.Skipped)
                        ? _workflow.Plan.ToDisplayBlock()
                        : $"Error: step {step} not found.";
                default:
                    return "Error: action must be list|done|doing|skip|set|clear";
            }
        }

        private static string BuildApprovalSummary(string name, JsonElement root) => name switch
        {
            "write_file" => "write " + GetString(root, "path"),
            "str_replace" => "patch " + GetString(root, "path"),
            "apply_patch" => "apply_patch",
            "write_files" => "write_files",
            "run_shell" => GetString(root, "command"),
            "download_file" => GetString(root, "url") + " → " + GetString(root, "path"),
            "fetch_url" => "fetch " + GetString(root, "url"),
            "package_install" => GetString(root, "ecosystem") + " " + GetString(root, "package"),
            "docker_run" => "docker: " + Truncate(GetString(root, "command"), 50),
            "git_commit" => "commit: " + GetString(root, "message"),
            "git_checkout" => "checkout " + GetString(root, "branch"),
            "spawn_subagent" => GetString(root, "kind") + ": " + Truncate(GetString(root, "task"), 60),
            _ => name
        };

        private async Task<string> SpawnSubagentAsync(JsonElement root, CancellationToken token)
        {
            if (SubagentHandler == null)
                return "Error: subagents are not configured in this host.";

            string kindRaw = GetString(root, "kind").Trim().ToLowerInvariant();
            string task = GetString(root, "task");
            if (string.IsNullOrWhiteSpace(task))
                return "Error: task is required for spawn_subagent.";

            SubagentKind kind = kindRaw switch
            {
                "explore" or "search" or "map" => SubagentKind.Explore,
                "tests" or "test" => SubagentKind.Tests,
                "fix" or "repair" => SubagentKind.Fix,
                _ => SubagentKind.Fix
            };

            SubagentResult result = await SubagentHandler(kind, task, token);
            var sb = new StringBuilder();
            sb.AppendLine($"subagent: {result.Kind}");
            sb.AppendLine($"tool_calls: {result.ToolCalls}");
            if (result.WrittenPaths.Count > 0)
            {
                sb.AppendLine("written:");
                foreach (string p in result.WrittenPaths)
                    sb.AppendLine("  - " + p);
            }
            sb.AppendLine("--- summary ---");
            sb.AppendLine(result.Summary);
            return Trim(sb.ToString());
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..(max - 1)] + "…");

        private async Task<string> WebSearchAsync(JsonElement root, CancellationToken token)
        {
            if (!WebSearchEnabled)
                return "Error: web search is disabled. Enable it with /tools web-search on.";

            string query = GetString(root, "query");
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query is required.";

            string results = await _webSearch.SearchTopSnippetsForNormalChatAsync(query, token);
            return string.IsNullOrWhiteSpace(results)
                ? "No web results found for that query."
                : results;
        }

        private async Task<string> RunShellAsync(JsonElement root, CancellationToken token)
        {
            string command = GetString(root, "command");
            if (string.IsNullOrWhiteSpace(command))
                return "Error: command is required.";

            string cwdArg = GetString(root, "working_directory");
            string cwd = string.IsNullOrWhiteSpace(cwdArg)
                ? _workspace.PrimaryRoot
                : _workspace.ResolvePath(cwdArg);

            if (!_workspace.IsPathAllowed(cwd))
                return $"Error: working directory is outside attached workspaces: {cwd}";
            if (!Directory.Exists(cwd))
                return $"Error: working directory does not exist: {cwd}";

            if (!_workspace.TryValidateShellCommand(command, cwd, out string sandboxReason))
                return $"Error: sandbox blocked command — {sandboxReason}";
            if (!ShellPolicyCached.TryAuthorize(command, out string policyReason))
                return $"Error: shell policy — {policyReason}";

            int timeout = GetInt(root, "timeout_seconds", DefaultShellTimeoutSeconds);
            timeout = Math.Clamp(timeout, 5, 300);

            // Pin the process to the allowed cwd and force an in-sandbox start location so
            // the model cannot inherit a host cwd outside the chosen workspace.
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (OperatingSystem.IsWindows())
            {
                string shell = ResolveWindowsShell();
                string safeCwd = cwd.Replace("'", "''");
                // Force location, then run user command. Set-Location overrides still validated above.
                string wrapped =
                    $"Set-Location -LiteralPath '{safeCwd}'; " +
                    "$ErrorActionPreference = 'Continue'; " +
                    command;
                psi.FileName = shell;
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(wrapped);
            }
            else
            {
                // Prefer bash when present (macOS/Linux); fall back to POSIX sh.
                string shell = ResolveUnixShell();
                string safeCwd = cwd.Replace("'", "'\\''");
                psi.FileName = shell;
                // bash -lc / sh -c both accept a single command string.
                if (shell.EndsWith("bash", StringComparison.Ordinal))
                {
                    psi.ArgumentList.Add("-lc");
                    psi.ArgumentList.Add($"cd '{safeCwd}' && {command}");
                }
                else
                {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add($"cd '{safeCwd}' && {command}");
                }
            }

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return $"Error: command timed out after {timeout}s.\nPartial stdout:\n{Trim(stdout.ToString())}\nPartial stderr:\n{Trim(stderr.ToString())}";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"exit_code: {process.ExitCode}");
            sb.AppendLine($"cwd: {cwd}");
            sb.AppendLine("--- stdout ---");
            sb.AppendLine(Trim(stdout.ToString()));
            if (stderr.Length > 0)
            {
                sb.AppendLine("--- stderr ---");
                sb.AppendLine(Trim(stderr.ToString()));
            }
            return sb.ToString();
        }

        private string ReadFile(JsonElement root)
        {
            string path = _workspace.ResolvePath(GetString(root, "path"));
            // Re-normalize and re-check after full resolution so ".." cannot slip out.
            if (!WorkspaceSession.TryNormalizePath(path, out path) || !_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";
            if (!File.Exists(path))
                return $"Error: file not found: {path}";

            int offset = GetInt(root, "offset", 0);
            int limit = GetInt(root, "limit", 0);

            if (offset > 0 || limit > 0)
            {
                string[] lines = File.ReadAllLines(path);
                int start = offset > 0 ? Math.Clamp(offset - 1, 0, lines.Length) : 0;
                int take = limit > 0 ? Math.Clamp(limit, 1, 5000) : lines.Length - start;
                var slice = lines.Skip(start).Take(take).ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"[[FILE]] {path} lines {start + 1}-{start + slice.Count} of {lines.Length}");
                for (int i = 0; i < slice.Count; i++)
                    sb.AppendLine($"{start + i + 1,6}| {slice[i]}");
                if (start + slice.Count < lines.Length)
                    sb.AppendLine($"...[use offset={start + slice.Count + 1} to continue]");
                return Trim(sb.ToString());
            }

            string text = File.ReadAllText(path);
            if (text.Length > MaxOutputChars)
                return text[..MaxOutputChars] + $"\n...[truncated, {text.Length} chars total]";
            return text;
        }

        private string StrReplace(JsonElement root)
        {
            string path = _workspace.ResolvePath(GetString(root, "path"));
            if (!WorkspaceSession.TryNormalizePath(path, out path) || !_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";

            string oldText = GetString(root, "old_string");
            string newText = GetString(root, "new_string");
            bool replaceAll = root.TryGetProperty("replace_all", out JsonElement ra) && ra.ValueKind == JsonValueKind.True;

            bool existed = File.Exists(path);
            string? before = null;
            try
            {
                if (existed && new FileInfo(path).Length < 400_000)
                    before = File.ReadAllText(path);
            }
            catch { /* ignore */ }

            _undo.RecordBeforeWrite(path);
            _workflow.Changes.NoteBeforeWrite(path, before, existed);
            string result = ApplyPatchService.StrReplace(path, oldText, newText, replaceAll);
            if (!result.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string after = File.ReadAllText(path);
                    _workflow.Changes.NoteAfterWrite(path, after);
                    lock (_writeGate)
                    {
                        _writtenPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                        _writtenPaths.Add(path);
                    }
                }
                catch { /* ignore */ }
            }
            return result;
        }

        private string ApplyPatch(JsonElement root)
        {
            string patch = GetString(root, "patch");
            // Snapshot files mentioned before apply (best-effort for undo).
            foreach (Match m in Regex.Matches(patch, @"^\*\*\* (?:Update|Add|Delete) File:\s*(.+)$", RegexOptions.Multiline))
            {
                try
                {
                    string p = _workspace.ResolvePath(m.Groups[1].Value.Trim());
                    if (_workspace.IsPathAllowed(p))
                    {
                        _undo.RecordBeforeWrite(p);
                        bool existed = File.Exists(p);
                        string? before = null;
                        try
                        {
                            if (existed && new FileInfo(p).Length < 400_000)
                                before = File.ReadAllText(p);
                        }
                        catch { /* ignore */ }
                        _workflow.Changes.NoteBeforeWrite(p, before, existed);
                    }
                }
                catch { /* ignore */ }
            }

            string result = ApplyPatchService.ApplyStructuredPatch(
                _workspace.PrimaryRoot,
                patch,
                rel =>
                {
                    string p = _workspace.ResolvePath(rel);
                    if (!WorkspaceSession.TryNormalizePath(p, out p) || !_workspace.IsPathAllowed(p))
                        throw new InvalidOperationException($"path outside workspace: {rel}");
                    return p;
                });

            // Refresh after content for known written paths
            foreach (string line in result.Split('\n'))
            {
                if (line.StartsWith("updated ", StringComparison.Ordinal) || line.StartsWith("added ", StringComparison.Ordinal))
                {
                    string p = line.Split(' ', 2).Length > 1 ? line.Split(' ', 2)[1].Trim() : "";
                    if (p.Length > 0 && File.Exists(p))
                    {
                        try
                        {
                            _workflow.Changes.NoteAfterWrite(p, File.ReadAllText(p));
                            lock (_writeGate)
                            {
                                _writtenPaths.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                                _writtenPaths.Add(p);
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            return result;
        }

        private string WriteFiles(JsonElement root)
        {
            if (!root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                return "Error: files array is required.";

            var sb = new StringBuilder();
            int ok = 0, fail = 0;
            foreach (JsonElement item in files.EnumerateArray())
            {
                string path = GetString(item, "path");
                string content = GetString(item, "content");
                if (string.IsNullOrWhiteSpace(path))
                {
                    fail++;
                    sb.AppendLine("fail: missing path");
                    continue;
                }

                // Reuse WriteFile logic via a synthetic element is hard; inline.
                string full = _workspace.ResolvePath(path);
                if (!WorkspaceSession.TryNormalizePath(full, out full) || !_workspace.IsPathAllowed(full))
                {
                    fail++;
                    sb.AppendLine($"fail outside workspace: {path}");
                    continue;
                }
                try
                {
                    string? dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    bool existed = File.Exists(full);
                    string? before = null;
                    try
                    {
                        if (existed && new FileInfo(full).Length < 400_000)
                            before = File.ReadAllText(full);
                    }
                    catch { /* ignore */ }
                    _undo.RecordBeforeWrite(full);
                    _workflow.Changes.NoteBeforeWrite(full, before, existed);
                    File.WriteAllText(full, content ?? string.Empty);
                    _workflow.Changes.NoteAfterWrite(full, content ?? string.Empty);
                    lock (_writeGate)
                    {
                        _writtenPaths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
                        _writtenPaths.Add(full);
                    }
                    ok++;
                    sb.AppendLine($"wrote {full} ({(content ?? "").Length} chars)");
                }
                catch (Exception ex)
                {
                    fail++;
                    sb.AppendLine($"fail {path}: {ex.Message}");
                }
            }
            sb.Insert(0, $"write_files: ok={ok} fail={fail}\n");
            return sb.ToString().TrimEnd();
        }

        private string WriteFile(JsonElement root)
        {
            string path = _workspace.ResolvePath(GetString(root, "path"));
            if (!WorkspaceSession.TryNormalizePath(path, out path) || !_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";

            // Parent directory must also stay inside the sandbox (blocks write via symlink tricks).
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !_workspace.IsPathAllowed(dir))
                return $"Error: parent directory outside attached workspaces: {dir}";
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string content = GetString(root, "content");
            bool existed = File.Exists(path);
            _undo.RecordBeforeWrite(path);
            string? before = null;
            try
            {
                if (existed && new FileInfo(path).Length < 400_000)
                    before = File.ReadAllText(path);
            }
            catch { /* ignore */ }

            _workflow.Changes.NoteBeforeWrite(path, before, existed);
            File.WriteAllText(path, content ?? string.Empty);
            _workflow.Changes.NoteAfterWrite(path, content ?? string.Empty);
            lock (_writeGate)
            {
                _writtenPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                _writtenPaths.Add(path);
            }

            string diffHint = string.Empty;
            if (before != null)
            {
                try
                {
                    var entries = Workspace.LineDiff.Build(before, content ?? string.Empty);
                    var diff = new StringBuilder();
                    int shown = 0;
                    foreach (var e in entries)
                    {
                        if (e.Kind == Workspace.LineDiffKind.Unchanged)
                            continue;
                        char mark = e.Kind == Workspace.LineDiffKind.Added ? '+' : '-';
                        diff.Append(mark).Append(' ').AppendLine(e.Text);
                        if (++shown >= 40)
                        {
                            diff.AppendLine("...[diff truncated]");
                            break;
                        }
                    }
                    if (shown > 0)
                        diffHint = "\n--- diff preview ---\n" + diff.ToString().TrimEnd();
                }
                catch { /* optional */ }
            }

            return $"Wrote {content?.Length ?? 0} chars to {path}{diffHint}";
        }

        private string ListDir(JsonElement root)
        {
            string path = string.IsNullOrWhiteSpace(GetString(root, "path"))
                ? _workspace.PrimaryRoot
                : _workspace.ResolvePath(GetString(root, "path"));

            if (!_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";
            if (!Directory.Exists(path))
                return $"Error: directory not found: {path}";

            bool recursive = root.TryGetProperty("recursive", out JsonElement rec) && rec.ValueKind == JsonValueKind.True;
            var sb = new StringBuilder();
            sb.AppendLine(path);

            IEnumerable<string> entries = recursive
                ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                : Directory.EnumerateFileSystemEntries(path);

            int count = 0;
            foreach (string entry in entries)
            {
                if (count++ >= 400)
                {
                    sb.AppendLine("...[truncated]");
                    break;
                }

                string rel = Path.GetRelativePath(path, entry).Replace('\\', '/');
                bool isDir = Directory.Exists(entry);
                sb.AppendLine(isDir ? $"dir  {rel}/" : $"file {rel}");
            }

            return Trim(sb.ToString());
        }

        private async Task<string> DownloadAsync(JsonElement root, CancellationToken token)
        {
            string url = GetString(root, "url");
            string path = _workspace.ResolvePath(GetString(root, "path"));

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "Error: url must be an absolute http(s) URL.";

            if (!_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, token);

            var info = new FileInfo(path);
            return $"Downloaded {info.Length} bytes to {path}";
        }

        private async Task<string> SearchFilesAsync(JsonElement root, CancellationToken token)
        {
            string query = GetString(root, "query");
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query is required.";

            string path = string.IsNullOrWhiteSpace(GetString(root, "path"))
                ? _workspace.PrimaryRoot
                : _workspace.ResolvePath(GetString(root, "path"));

            if (!_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";
            if (!Directory.Exists(path))
                return $"Error: directory not found: {path}";

            string? glob = GetString(root, "glob");
            bool regex = root.TryGetProperty("regex", out JsonElement re) && re.ValueKind == JsonValueKind.True;
            int maxHits = 80;
            if (root.TryGetProperty("max_hits", out JsonElement mh) && mh.TryGetInt32(out int m))
                maxHits = m;
            int offset = GetInt(root, "offset", 0);
            // Fetch extra then skip for simple pagination
            int fetch = Math.Clamp(maxHits + Math.Max(0, offset), 1, 400);
            string result = await WorkspaceSearchService.SearchAsync(path, query, glob, regex, fetch, token);
            if (offset <= 0)
                return result;

            var lines = result.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            // Keep header-ish first line if present
            var body = lines.Skip(offset).Take(maxHits).ToList();
            if (body.Count == 0)
                return $"No more matches (offset={offset}).";
            return string.Join('\n', body)
                   + (lines.Count > offset + maxHits ? $"\n...[more; use offset={offset + maxHits}]" : "");
        }

        private string ReadCsvTool(JsonElement root)
        {
            string path = _workspace.ResolvePath(GetString(root, "path"));
            if (!WorkspaceSession.TryNormalizePath(path, out path) || !_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";
            return DataFileTools.ReadCsv(path, GetInt(root, "max_rows", 30));
        }

        private string ReadNotebookTool(JsonElement root)
        {
            string path = _workspace.ResolvePath(GetString(root, "path"));
            if (!WorkspaceSession.TryNormalizePath(path, out path) || !_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";
            return DataFileTools.ReadNotebook(path, GetInt(root, "max_cells", 20));
        }

        private async Task<string> FetchUrlAsync(JsonElement root, CancellationToken token)
        {
            string url = GetString(root, "url");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "Error: url must be an absolute http(s) URL.";

            int maxChars = GetInt(root, "max_chars", 12_000);
            maxChars = Math.Clamp(maxChars, 500, 48_000);

            using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            string media = response.Content.Headers.ContentType?.MediaType ?? "";
            string body = await response.Content.ReadAsStringAsync(token);

            string text;
            if (media.Contains("html", StringComparison.OrdinalIgnoreCase)
                || body.TrimStart().StartsWith("<!", StringComparison.OrdinalIgnoreCase)
                || body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(body);
                foreach (var n in doc.DocumentNode.SelectNodes("//script|//style|//noscript") ?? Enumerable.Empty<HtmlNode>())
                    n.Remove();
                text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
                text = Regex.Replace(text, @"[ \t]+\n", "\n");
                text = Regex.Replace(text, @"\n{3,}", "\n\n");
                text = text.Trim();
            }
            else
            {
                text = body;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[[FETCH]] {uri}");
            sb.AppendLine($"status: {(int)response.StatusCode} content-type: {media}");
            if (text.Length > maxChars)
                sb.Append(text.AsSpan(0, maxChars)).AppendLine().AppendLine("...[truncated]");
            else
                sb.AppendLine(text);
            return sb.ToString().TrimEnd();
        }

        private async Task<string> RunTestsTrackedAsync(JsonElement root, CancellationToken token)
        {
            string filter = GetString(root, "filter");
            string output = await RunTestsAsync(root, token);
            bool failed = output.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                || (output.Contains("exit_code: ", StringComparison.Ordinal)
                    && !output.Contains("exit_code: 0", StringComparison.Ordinal));
            if (failed)
                _workflow.NoteFailedTest(string.IsNullOrWhiteSpace(filter) ? "run_tests" : filter);
            else if (output.Contains("exit_code: 0", StringComparison.Ordinal)
                     || output.Contains("Passed!", StringComparison.OrdinalIgnoreCase))
                _workflow.NoteTestsPassedClear(string.IsNullOrWhiteSpace(filter) ? null : filter);
            return output;
        }

        private async Task<string> RunTestsAsync(JsonElement root, CancellationToken token)
        {
            string filter = GetString(root, "filter");
            string prefer = GetString(root, "prefer");
            bool coverage = root.TryGetProperty("coverage", out JsonElement cov) && cov.ValueKind == JsonValueKind.True;
            string rootDir = _workspace.PrimaryRoot;

            prefer = prefer.Trim().ToLowerInvariant();
            bool hasCsproj = Directory.EnumerateFiles(rootDir, "*.sln", SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateFiles(rootDir, "*.csproj", SearchOption.AllDirectories).Take(1).Any();
            bool hasPackageJson = File.Exists(Path.Combine(rootDir, "package.json"));
            bool hasPy = File.Exists(Path.Combine(rootDir, "pyproject.toml"))
                || File.Exists(Path.Combine(rootDir, "requirements.txt"));
            bool hasCargo = File.Exists(Path.Combine(rootDir, "Cargo.toml"));
            bool hasGo = File.Exists(Path.Combine(rootDir, "go.mod"));

            string file, args, label;
            if (prefer is "dotnet" or "csharp" || (string.IsNullOrEmpty(prefer) && hasCsproj))
            {
                file = "dotnet";
                args = "test --nologo -v q";
                if (!string.IsNullOrWhiteSpace(filter))
                    args += $" --filter {filter}";
                if (coverage)
                    args += " --collect:\"XPlat Code Coverage\"";
                label = "dotnet test";
            }
            else if (prefer is "node" or "npm" || (string.IsNullOrEmpty(prefer) && hasPackageJson))
            {
                file = "npm";
                args = "test --silent";
                if (!string.IsNullOrWhiteSpace(filter))
                    args += $" -- {filter}";
                label = "npm test";
            }
            else if (prefer is "python" or "pytest" || (string.IsNullOrEmpty(prefer) && hasPy))
            {
                file = "pytest";
                args = "-q";
                if (!string.IsNullOrWhiteSpace(filter))
                    args += $" -k \"{filter}\"";
                if (coverage)
                    args += " --cov --cov-report=term-missing";
                label = "pytest";
            }
            else if (prefer is "rust" or "cargo" || (string.IsNullOrEmpty(prefer) && hasCargo))
            {
                file = "cargo";
                args = "test --quiet";
                if (!string.IsNullOrWhiteSpace(filter))
                    args += " " + filter;
                label = "cargo test";
            }
            else if (prefer is "go" || (string.IsNullOrEmpty(prefer) && hasGo))
            {
                file = "go";
                args = "test ./...";
                if (!string.IsNullOrWhiteSpace(filter))
                    args += $" -run {filter}";
                if (coverage)
                    args += " -cover";
                label = "go test";
            }
            else
            {
                return "Error: no known test runner. Pass prefer=dotnet|node|python|rust|go.";
            }

            // Reuse shell with policy
            var fake = JsonDocument.Parse($"{{\"command\":\"{EscapeJson(file + " " + args)}\",\"timeout_seconds\":180}}");
            string output = await RunShellAsync(fake.RootElement, token);
            var sb = new StringBuilder();
            sb.AppendLine($"[[RUN_TESTS]] {label}");
            if (!string.IsNullOrWhiteSpace(filter))
                sb.AppendLine($"filter: {filter}");
            sb.AppendLine(output);
            if (coverage)
                sb.AppendLine("(coverage requested — see output above if collector produced a summary)");
            return Trim(sb.ToString());
        }

        private async Task<string> PackageInstallAsync(JsonElement root, CancellationToken token)
        {
            string eco = GetString(root, "ecosystem").Trim().ToLowerInvariant();
            string package = GetString(root, "package").Trim();
            if (string.IsNullOrWhiteSpace(package))
                return "Error: package is required.";

            string cmd = eco switch
            {
                "dotnet" or "nuget" =>
                    string.IsNullOrWhiteSpace(GetString(root, "project"))
                        ? $"dotnet add package {package}"
                        : $"dotnet add \"{GetString(root, "project")}\" package {package}",
                "npm" or "node" or "yarn" => $"npm install {package}",
                "pip" or "python" => $"pip install {package}",
                _ => ""
            };
            if (string.IsNullOrEmpty(cmd))
                return "Error: ecosystem must be dotnet | npm | pip.";

            var fake = JsonDocument.Parse($"{{\"command\":\"{EscapeJson(cmd)}\",\"timeout_seconds\":300}}");
            return "[[PACKAGE_INSTALL]]\n" + await RunShellAsync(fake.RootElement, token);
        }

        private async Task<string> DockerRunAsync(JsonElement root, CancellationToken token)
        {
            if (!CommandExistsOnPath("docker") && !CommandExistsOnPath("docker.exe"))
                return "Error: docker not found on PATH.";

            string command = GetString(root, "command");
            if (string.IsNullOrWhiteSpace(command))
                return "Error: command is required.";

            string image = GetString(root, "image");
            string rootDir = _workspace.PrimaryRoot;
            if (string.IsNullOrWhiteSpace(image))
            {
                if (File.Exists(Path.Combine(rootDir, "Dockerfile")))
                    image = "axiom-local:dev";
                else if (File.Exists(Path.Combine(rootDir, ".devcontainer", "Dockerfile")))
                    image = "axiom-devcontainer:dev";
                else
                    image = "mcr.microsoft.com/dotnet/sdk:10.0";
            }

            // Prefer docker compose run if compose file exists
            string dockerCmd;
            if (File.Exists(Path.Combine(rootDir, "docker-compose.yml"))
                || File.Exists(Path.Combine(rootDir, "compose.yml")))
            {
                dockerCmd = $"docker compose run --rm --workdir /work app sh -c {QuoteSh(command)}";
            }
            else
            {
                string vol = OperatingSystem.IsWindows()
                    ? $"\"{rootDir}:/work\""
                    : $"{rootDir}:/work";
                dockerCmd = $"docker run --rm -v {vol} -w /work {image} sh -c {QuoteSh(command)}";
            }

            var fake = JsonDocument.Parse($"{{\"command\":\"{EscapeJson(dockerCmd)}\",\"timeout_seconds\":300}}");
            return "[[DOCKER_RUN]]\n" + await RunShellAsync(fake.RootElement, token);
        }

        private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string QuoteSh(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        private static JsonObject Schema(JsonObject properties, string[] required)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Length > 0)
            {
                var arr = new JsonArray();
                foreach (string r in required)
                    arr.Add(r);
                schema["required"] = arr;
            }
            return schema;
        }

        private static JsonObject Prop(string type, string description) => new()
        {
            ["type"] = type,
            ["description"] = description
        };

        private static string ResolveWindowsShell()
        {
            // Prefer Windows PowerShell 5.x (ubiquitous), then PowerShell 7+ if only that is installed.
            foreach (string candidate in new[] { "powershell.exe", "pwsh.exe", "pwsh" })
            {
                if (CommandExistsOnPath(candidate))
                    return candidate;
            }
            return "powershell.exe";
        }

        private static string ResolveUnixShell()
        {
            foreach (string candidate in new[] { "/bin/bash", "/usr/bin/bash", "bash", "/bin/sh", "/usr/bin/sh", "sh" })
            {
                if (candidate.Contains('/') && File.Exists(candidate))
                    return candidate;
                if (!candidate.Contains('/') && CommandExistsOnPath(candidate))
                    return candidate;
            }
            return "/bin/sh";
        }

        private static bool CommandExistsOnPath(string name)
        {
            try
            {
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrWhiteSpace(pathEnv))
                    return false;

                char sep = Path.PathSeparator;
                foreach (string dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string full = Path.Combine(dir.Trim(), name);
                        if (File.Exists(full))
                            return true;
                        if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(full + ".exe"))
                                return true;
                        }
                    }
                    catch { /* ignore bad PATH entries */ }
                }
            }
            catch { /* ignore */ }
            return false;
        }

        private static string GetString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement el))
                return string.Empty;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.ToString(),
                _ => string.Empty
            };
        }

        private static int GetInt(JsonElement root, string name, int fallback)
            => root.TryGetProperty(name, out JsonElement el) && el.TryGetInt32(out int v) ? v : fallback;

        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Length <= MaxOutputChars
                ? text
                : text[..MaxOutputChars] + $"\n...[truncated, {text.Length} chars total]";
        }
    }
}
