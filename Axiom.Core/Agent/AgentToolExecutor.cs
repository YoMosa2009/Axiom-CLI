using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Axiom.Core.Tools;

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
                    "Read a text file from an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "File path relative to primary workspace or absolute under an attached root")
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
                        ["max_hits"] = Prop("integer", "Max matches (default 80, max 200)")
                    }, required: ["query"])),

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
                    }, required: []))
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

                tools.Add(new(
                    "download_file",
                    "Download a URL into a path under an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["url"] = Prop("string", "HTTP/HTTPS URL"),
                        ["path"] = Prop("string", "Destination file path inside the workspace")
                    }, required: ["url", "path"])));

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
                && toolName is "write_file" or "download_file" or "web_search"
                    or "git_commit" or "git_checkout" or "worktree_create" or "worktree_remove"
                    or "spawn_subagent" or "run_background" or "open_pr")
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

                // Approval / plan gates for mutating tools.
                if (IsMutatingTool(name))
                {
                    string summary = BuildApprovalSummary(name, root);
                    if (ApprovalMode == ApprovalMode.Plan)
                        return $"Plan-only: would run {name} — {summary}";

                    bool forceAsk = ApprovalMode == ApprovalMode.Ask
                        || (ApprovalMode == ApprovalMode.Auto && name == "write_file" && IsBigWrite(root));

                    if (forceAsk && ApprovalHandler != null)
                    {
                        string detail = name == "write_file"
                            ? summary + " (large change — review recommended)"
                            : summary;
                        bool ok = await ApprovalHandler(
                            new ToolApprovalRequest(name, detail, argumentsJson ?? "{}"),
                            token);
                        if (!ok)
                            return $"Denied: user rejected {name} ({summary})";
                    }
                }

                _workflow.Replay.Record(name, argumentsJson ?? "{}");

                return name switch
                {
                    "run_shell" => await RunShellAsync(root, token),
                    "read_file" => ReadFile(root),
                    "write_file" => WriteFile(root),
                    "list_dir" => ListDir(root),
                    "download_file" => await DownloadAsync(root, token),
                    "search_files" => await SearchFilesAsync(root, token),
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
            "write_file" or "run_shell" or "download_file" or "git_commit" or "git_checkout"
            or "worktree_create" or "worktree_remove" or "spawn_subagent" or "run_background" or "open_pr";

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
            "run_shell" => GetString(root, "command"),
            "download_file" => GetString(root, "url") + " → " + GetString(root, "path"),
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

            string text = File.ReadAllText(path);
            if (text.Length > MaxOutputChars)
                return text[..MaxOutputChars] + $"\n...[truncated, {text.Length} chars total]";
            return text;
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

            return await WorkspaceSearchService.SearchAsync(path, query, glob, regex, maxHits, token);
        }

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
