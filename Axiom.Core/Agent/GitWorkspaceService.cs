using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    // First-class git operations for the coding agent (status/diff/log/commit/branch).
    public static class GitWorkspaceService
    {
        public static async Task<string> StatusAsync(string root, CancellationToken token)
            => await RunAsync(root, token, "status", "--short", "--branch");

        public static async Task<string> DiffAsync(string root, CancellationToken token, bool staged = false)
            => staged
                ? await RunAsync(root, token, "diff", "--staged")
                : await RunAsync(root, token, "diff");

        public static async Task<string> LogAsync(string root, CancellationToken token, int count = 12)
            => await RunAsync(root, token, "log", $"-{Math.Clamp(count, 1, 50)}", "--oneline", "--decorate");

        public static async Task<string> BranchAsync(string root, CancellationToken token)
            => await RunAsync(root, token, "branch", "-vv");

        public static async Task<string> CommitAsync(string root, string message, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "Error: commit message is required.";

            string add = await RunAsync(root, token, "add", "-A");
            string commit = await RunAsync(root, token, "commit", "-m", message.Trim());
            return $"git add -A:\n{add}\n\ngit commit:\n{commit}";
        }

        public static async Task<string> CheckoutBranchAsync(string root, string branch, bool create, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(branch))
                return "Error: branch name required.";
            return create
                ? await RunAsync(root, token, "checkout", "-b", branch.Trim())
                : await RunAsync(root, token, "checkout", branch.Trim());
        }

        public static bool IsGitRepo(string root)
        {
            try
            {
                string git = Path.Combine(root, ".git");
                return Directory.Exists(git) || File.Exists(git);
            }
            catch { return false; }
        }

        private static async Task<string> RunAsync(string root, CancellationToken token, params string[] args)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return "Error: workspace root not found.";
            if (!IsGitRepo(root))
                return "Error: not a git repository (no .git in workspace root).";

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            try
            {
                using var process = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                linked.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    try { process.Kill(true); } catch { }
                    return "Error: git command timed out.";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"$ git {string.Join(' ', args)}");
                sb.AppendLine($"exit_code: {process.ExitCode}");
                if (stdout.Length > 0)
                    sb.Append(stdout);
                if (stderr.Length > 0)
                    sb.Append(stderr);
                string text = sb.ToString().Trim();
                return text.Length > 40_000 ? text[..40_000] + "\n...[truncated]" : text;
            }
            catch (Exception ex)
            {
                return $"Error running git: {ex.Message}";
            }
        }
    }
}
