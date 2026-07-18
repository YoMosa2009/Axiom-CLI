using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    // Detect project type and run structured build/test diagnostics for Critic/Builder evidence.
    public static class DiagnosticsService
    {
        public static async Task<string> RunAsync(string root, CancellationToken token, string? prefer = null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return "Error: workspace root not found.";

            prefer = (prefer ?? string.Empty).Trim().ToLowerInvariant();
            var commands = new List<(string Label, string FileName, string Arguments)>();

            bool hasCsproj = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Take(1).Any();
            bool hasPackageJson = File.Exists(Path.Combine(root, "package.json"));
            bool hasPyProject = File.Exists(Path.Combine(root, "pyproject.toml"))
                || File.Exists(Path.Combine(root, "requirements.txt"))
                || Directory.EnumerateFiles(root, "pytest.ini", SearchOption.TopDirectoryOnly).Any();
            bool hasCargo = File.Exists(Path.Combine(root, "Cargo.toml"));
            bool hasGo = File.Exists(Path.Combine(root, "go.mod"));

            if (prefer is "dotnet" or "csharp" || (string.IsNullOrEmpty(prefer) && hasCsproj))
            {
                commands.Add(("dotnet build", "dotnet", "build --nologo -v q"));
                commands.Add(("dotnet test", "dotnet", "test --nologo -v q --no-build"));
            }
            else if (prefer is "node" or "npm" or "js" || (string.IsNullOrEmpty(prefer) && hasPackageJson))
            {
                commands.Add(("npm test", "npm", "test --silent"));
                if (File.Exists(Path.Combine(root, "node_modules", "typescript", "bin", "tsc"))
                    || File.Exists(Path.Combine(root, "tsconfig.json")))
                    commands.Add(("npx tsc --noEmit", "npx", "tsc --noEmit"));
            }
            else if (prefer is "python" or "pytest" || (string.IsNullOrEmpty(prefer) && hasPyProject))
            {
                commands.Add(("pytest", "pytest", "-q"));
            }
            else if (prefer is "rust" or "cargo" || (string.IsNullOrEmpty(prefer) && hasCargo))
            {
                commands.Add(("cargo test", "cargo", "test --quiet"));
            }
            else if (prefer is "go" || (string.IsNullOrEmpty(prefer) && hasGo))
            {
                commands.Add(("go test", "go", "test ./..."));
            }
            else if (hasCsproj)
            {
                commands.Add(("dotnet build", "dotnet", "build --nologo -v q"));
            }
            else
            {
                return "No known project markers (*.sln/*.csproj, package.json, pyproject.toml, Cargo.toml, go.mod). " +
                       "Pass prefer=dotnet|node|python|rust|go or run_shell yourself.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("[[DIAGNOSTICS]]");
            sb.AppendLine($"root: {root}");

            foreach ((string label, string file, string args) in commands.Take(3))
            {
                token.ThrowIfCancellationRequested();
                sb.AppendLine();
                sb.AppendLine($"### {label}");
                string result = await RunProcessAsync(root, file, args, token);
                sb.AppendLine(result);
            }

            sb.AppendLine("[[END DIAGNOSTICS]]");
            string text = sb.ToString();
            return text.Length > 48_000 ? text[..48_000] + "\n...[truncated]" : text;
        }

        private static async Task<string> RunProcessAsync(string root, string fileName, string arguments, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

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
                linked.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    try { process.Kill(true); } catch { }
                    return "timed out (3m)";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"exit_code: {process.ExitCode}");
                if (stdout.Length > 0) sb.Append(stdout);
                if (stderr.Length > 0) sb.Append(stderr);
                string t = sb.ToString().Trim();
                return t.Length > 12_000 ? t[..12_000] + "\n...[truncated]" : t;
            }
            catch (Exception ex)
            {
                return $"failed to start {fileName}: {ex.Message}";
            }
        }
    }
}
