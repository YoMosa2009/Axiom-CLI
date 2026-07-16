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

namespace Axiom.Core.Agent
{
    // Executes agent tool calls (shell, files, download) inside the user-attached workspaces.
    public sealed class AgentToolExecutor
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private const int MaxOutputChars = 48_000;
        private const int DefaultShellTimeoutSeconds = 120;

        private readonly WorkspaceSession _workspace;

        public AgentToolExecutor(WorkspaceSession workspace)
        {
            _workspace = workspace;
        }

        public static IReadOnlyList<OpenRouterToolDefinition> GetToolDefinitions()
        {
            return
            [
                new OpenRouterToolDefinition(
                    "run_shell",
                    "Run a shell command in the workspace (build, install, git, scripts, tests, etc.).",
                    Schema(new JsonObject
                    {
                        ["command"] = Prop("string", "Shell command to execute"),
                        ["working_directory"] = Prop("string", "Optional cwd relative to or under an attached workspace"),
                        ["timeout_seconds"] = Prop("integer", "Optional timeout (default 120, max 300)")
                    }, required: ["command"])),

                new OpenRouterToolDefinition(
                    "read_file",
                    "Read a text file from an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "File path relative to primary workspace or absolute under an attached root")
                    }, required: ["path"])),

                new OpenRouterToolDefinition(
                    "write_file",
                    "Create or overwrite a text file inside an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "Target file path"),
                        ["content"] = Prop("string", "Full file contents to write")
                    }, required: ["path", "content"])),

                new OpenRouterToolDefinition(
                    "list_dir",
                    "List files and folders in a directory under an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["path"] = Prop("string", "Directory path (default: primary workspace root)"),
                        ["recursive"] = Prop("boolean", "If true, list nested entries (capped)")
                    }, required: [])),

                new OpenRouterToolDefinition(
                    "download_file",
                    "Download a URL into a path under an attached workspace.",
                    Schema(new JsonObject
                    {
                        ["url"] = Prop("string", "HTTP/HTTPS URL"),
                        ["path"] = Prop("string", "Destination file path inside the workspace")
                    }, required: ["url", "path"])),

                new OpenRouterToolDefinition(
                    "search_files",
                    "Search for a text pattern across files in an attached workspace directory.",
                    Schema(new JsonObject
                    {
                        ["query"] = Prop("string", "Text to search for"),
                        ["path"] = Prop("string", "Directory to search (default: primary workspace)"),
                        ["glob"] = Prop("string", "Optional file extension filter e.g. *.cs")
                    }, required: ["query"]))
            ];
        }

        public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken token)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                JsonElement root = doc.RootElement;

                return toolName switch
                {
                    "run_shell" => await RunShellAsync(root, token),
                    "read_file" => ReadFile(root),
                    "write_file" => WriteFile(root),
                    "list_dir" => ListDir(root),
                    "download_file" => await DownloadAsync(root, token),
                    "search_files" => SearchFiles(root),
                    _ => $"Unknown tool: {toolName}"
                };
            }
            catch (Exception ex)
            {
                return $"Tool error ({toolName}): {ex.Message}";
            }
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

            int timeout = GetInt(root, "timeout_seconds", DefaultShellTimeoutSeconds);
            timeout = Math.Clamp(timeout, 5, 300);

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
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.FileName = "/bin/bash";
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(command);
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
            if (!_workspace.IsPathAllowed(path))
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
            if (!_workspace.IsPathAllowed(path))
                return $"Error: path outside attached workspaces: {path}";

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string content = GetString(root, "content");
            File.WriteAllText(path, content ?? string.Empty);
            return $"Wrote {content?.Length ?? 0} chars to {path}";
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

        private string SearchFiles(JsonElement root)
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
            var sb = new StringBuilder();
            int hits = 0;

            foreach (string file in Directory.EnumerateFiles(path, string.IsNullOrWhiteSpace(glob) ? "*" : glob, SearchOption.AllDirectories))
            {
                if (!_workspace.IsPathAllowed(file))
                    continue;

                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".zip" or ".pdf")
                    continue;

                string text;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 1_500_000)
                        continue;
                    text = File.ReadAllText(file);
                }
                catch { continue; }

                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        string rel = Path.GetRelativePath(path, file).Replace('\\', '/');
                        string line = lines[i].TrimEnd('\r');
                        if (line.Length > 200)
                            line = line[..200] + "...";
                        sb.AppendLine($"{rel}:{i + 1}: {line}");
                        if (++hits >= 80)
                            return Trim(sb.ToString()) + "\n...[truncated]";
                    }
                }
            }

            return hits == 0 ? "No matches." : Trim(sb.ToString());
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
