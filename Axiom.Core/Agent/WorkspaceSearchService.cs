using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    // Ripgrep-class search: prefer `rg` when on PATH, else fast managed walk with ignore rules.
    public static class WorkspaceSearchService
    {
        private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "packages",
            "dist", "build", "coverage", ".next", ".nuxt", ".turbo", "target", "__pycache__",
            ".venv", "venv", "vendor"
        };

        private static readonly HashSet<string> IgnoredExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico",
            ".zip", ".7z", ".rar", ".pdf", ".woff", ".woff2", ".bin", ".so", ".dylib",
            ".o", ".a", ".class", ".jar", ".map"
        };

        public static async Task<string> SearchAsync(
            string root,
            string query,
            string? glob,
            bool regex,
            int maxHits,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query is required.";
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return "Error: search root not found.";

            maxHits = Math.Clamp(maxHits, 1, 200);

            string? rg = FindExecutable("rg") ?? FindExecutable("rg.exe");
            if (rg != null)
            {
                string rgResult = await RunRipgrepAsync(rg, root, query, glob, regex, maxHits, token);
                if (!rgResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                    && !rgResult.Contains("failed to start", StringComparison.OrdinalIgnoreCase))
                    return rgResult;
            }

            return ManagedSearch(root, query, glob, regex, maxHits, token);
        }

        private static async Task<string> RunRipgrepAsync(
            string rgPath,
            string root,
            string query,
            string? glob,
            bool regex,
            int maxHits,
            CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = rgPath,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--line-number");
            psi.ArgumentList.Add("--no-heading");
            psi.ArgumentList.Add("--color");
            psi.ArgumentList.Add("never");
            psi.ArgumentList.Add("--max-count");
            psi.ArgumentList.Add("5");
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(maxHits.ToString());
            if (!regex)
                psi.ArgumentList.Add("-F");
            psi.ArgumentList.Add("-i");
            if (!string.IsNullOrWhiteSpace(glob))
            {
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add(glob!);
            }
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(query);
            psi.ArgumentList.Add(".");

            try
            {
                using var process = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                linked.CancelAfter(TimeSpan.FromSeconds(45));
                await process.WaitForExitAsync(linked.Token);
                string text = stdout.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return process.ExitCode == 1 ? "No matches." : "No matches.";
                return text.Length > 40_000 ? text[..40_000] + "\n...[truncated]" : text;
            }
            catch (Exception ex)
            {
                return $"Error: rg failed ({ex.Message}); falling back.";
            }
        }

        private static string ManagedSearch(string root, string query, string? glob, bool regex, int maxHits, CancellationToken token)
        {
            Regex? re = null;
            if (regex)
            {
                try { re = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
                catch (Exception ex) { return $"Error: invalid regex: {ex.Message}"; }
            }

            var sb = new StringBuilder();
            int hits = 0;
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0 && hits < maxHits)
            {
                token.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                IEnumerable<string> subDirs;
                IEnumerable<string> files;
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir);
                    files = Directory.EnumerateFiles(dir);
                }
                catch { continue; }

                foreach (string d in subDirs)
                {
                    string name = Path.GetFileName(d);
                    if (!IgnoredDirs.Contains(name))
                        pending.Push(d);
                }

                foreach (string file in files)
                {
                    if (hits >= maxHits)
                        break;
                    string ext = Path.GetExtension(file);
                    if (IgnoredExt.Contains(ext))
                        continue;
                    if (!string.IsNullOrWhiteSpace(glob) && !GlobMatch(Path.GetFileName(file), glob!))
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
                    int fileHits = 0;
                    for (int i = 0; i < lines.Length && hits < maxHits && fileHits < 5; i++)
                    {
                        string line = lines[i];
                        bool match = re != null
                            ? re.IsMatch(line)
                            : line.Contains(query, StringComparison.OrdinalIgnoreCase);
                        if (!match)
                            continue;

                        string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                        string clipped = line.TrimEnd('\r');
                        if (clipped.Length > 220)
                            clipped = clipped[..220] + "...";
                        sb.AppendLine($"{rel}:{i + 1}:{clipped}");
                        hits++;
                        fileHits++;
                    }
                }
            }

            if (hits == 0)
                return "No matches.";
            if (hits >= maxHits)
                sb.AppendLine("...[truncated]");
            return sb.ToString().TrimEnd();
        }

        private static bool GlobMatch(string fileName, string pattern)
        {
            // Simple *.ext or exact name.
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
        }

        private static string? FindExecutable(string name)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string ext = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? ".exe" : string.Empty;
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                string full = Path.Combine(dir, name + ext);
                if (File.Exists(full))
                    return full;
            }
            return null;
        }
    }
}
