using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    public sealed record GitBranchSnapshot(
        bool IsRepo,
        string Branch,
        bool Dirty,
        int ChangedFileCount,
        string ShortStatus);

    public static class GitBranchContext
    {
        public static async Task<GitBranchSnapshot> CaptureAsync(string root, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || !GitWorkspaceService.IsGitRepo(root))
                return new GitBranchSnapshot(false, "", false, 0, "");

            string branch = (await RunGit(root, token, "branch", "--show-current")).Trim();
            if (string.IsNullOrWhiteSpace(branch))
                branch = (await RunGit(root, token, "rev-parse", "--abbrev-ref", "HEAD")).Trim();

            string status = (await RunGit(root, token, "status", "--porcelain")).Trim();
            int changed = string.IsNullOrWhiteSpace(status)
                ? 0
                : status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            return new GitBranchSnapshot(true, branch, changed > 0, changed,
                status.Length > 800 ? status[..800] + "\n..." : status);
        }

        public static string ToPromptBlock(GitBranchSnapshot snap)
        {
            if (!snap.IsRepo)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[[GIT BRANCH CONTEXT]]");
            sb.AppendLine($"branch: {snap.Branch}");
            sb.AppendLine($"dirty: {(snap.Dirty ? "yes" : "no")} ({snap.ChangedFileCount} path(s))");
            if (snap.Dirty && !string.IsNullOrWhiteSpace(snap.ShortStatus))
            {
                sb.AppendLine("status:");
                sb.AppendLine(snap.ShortStatus);
            }
            if (snap.Dirty)
                sb.AppendLine("Warn the user before large rewrites on a dirty tree; prefer minimal diffs.");
            sb.AppendLine("[[END GIT BRANCH CONTEXT]]");
            return sb.ToString();
        }

        public static async Task<string> CreatePullRequestAsync(
            string root,
            string title,
            string body,
            CancellationToken token)
        {
            if (!GitWorkspaceService.IsGitRepo(root))
                return "Error: not a git repository.";

            // Prefer GitHub CLI when available.
            string? gh = FindOnPath("gh") ?? FindOnPath("gh.exe");
            if (gh == null)
                return "Error: GitHub CLI (gh) not found on PATH. Install gh or push manually.";

            string t = string.IsNullOrWhiteSpace(title) ? "Axiom changes" : title.Trim();
            string b = string.IsNullOrWhiteSpace(body) ? "Changes prepared by Axiom CLI." : body.Trim();

            // Ensure branch is pushed
            string push = await RunProcess(root, "git", "push -u origin HEAD", token);
            string pr = await RunProcess(root, gh,
                $"pr create --title {Quote(t)} --body {Quote(b)}", token);
            return $"git push:\n{push}\n\ngh pr create:\n{pr}";
        }

        private static async Task<string> RunGit(string root, CancellationToken token, params string[] args)
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
                using var p = Process.Start(psi);
                if (p == null)
                    return "";
                string o = await p.StandardOutput.ReadToEndAsync(token);
                await p.WaitForExitAsync(token);
                return o;
            }
            catch
            {
                return "";
            }
        }

        private static async Task<string> RunProcess(string root, string file, string args, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null)
                    return "failed to start";
                string o = await p.StandardOutput.ReadToEndAsync(token);
                string e = await p.StandardError.ReadToEndAsync(token);
                await p.WaitForExitAsync(token);
                return $"exit_code: {p.ExitCode}\n{o}{e}".Trim();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private static string? FindOnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    string full = Path.Combine(dir, name);
                    if (File.Exists(full))
                        return full;
                }
                catch { /* ignore */ }
            }
            return null;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
