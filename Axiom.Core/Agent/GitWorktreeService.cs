using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    // Optional isolated apply surface: create a git worktree branch for agent edits.
    public static class GitWorktreeService
    {
        public static async Task<string> CreateAsync(string repoRoot, string? branchName, CancellationToken token)
        {
            if (!GitWorkspaceService.IsGitRepo(repoRoot))
                return "Error: not a git repository.";

            branchName = string.IsNullOrWhiteSpace(branchName)
                ? "axiom/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                : branchName.Trim();

            string parent = Path.GetDirectoryName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                ?? repoRoot;
            string worktreePath = Path.Combine(parent, Path.GetFileName(repoRoot) + "-axiom-" + Guid.NewGuid().ToString("N")[..8]);

            string result = await RunGitAsync(repoRoot, token,
                "worktree", "add", "-b", branchName, worktreePath);
            if (result.Contains("exit_code: 0", StringComparison.Ordinal) || Directory.Exists(worktreePath))
            {
                return $"Created worktree\npath: {worktreePath}\nbranch: {branchName}\n\n{result}\n" +
                       "Agent tools should use this path as the exclusive workspace until you merge.";
            }

            return $"Failed to create worktree.\n{result}";
        }

        public static async Task<string> ListAsync(string repoRoot, CancellationToken token)
            => await RunGitAsync(repoRoot, token, "worktree", "list");

        public static async Task<string> RemoveAsync(string repoRoot, string worktreePath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(worktreePath))
                return "Error: worktree path required.";
            return await RunGitAsync(repoRoot, token, "worktree", "remove", "--force", worktreePath.Trim());
        }

        private static async Task<string> RunGitAsync(string root, CancellationToken token, params string[] args)
        {
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
                using var p = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                linked.CancelAfter(TimeSpan.FromSeconds(90));
                await p.WaitForExitAsync(linked.Token);
                return $"exit_code: {p.ExitCode}\n{stdout}{stderr}".Trim();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
