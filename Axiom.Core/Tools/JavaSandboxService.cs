using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Axiom.Core.Tools
{
    // Extracted from the WPF app's inline WorkplaceView.RunJavaAsync/RunProcessAsync/FindExecutable
    // (WorkplaceView.xaml.cs). Those were already UI-free static helpers, just embedded in a
    // WPF code-behind file — this is a straight lift into a standalone service. Shells out to
    // `javac`/`java` resolved from PATH, so it works wherever a JDK is installed, on any OS.
    public static class JavaSandboxService
    {
        private const int TimeoutMs = 15_000;

        public static async Task<string> RunAsync(string code, string tempDir)
        {
            Directory.CreateDirectory(tempDir);

            Match classMatch = Regex.Match(code, @"public\s+class\s+(\w+)");
            string className = classMatch.Success ? classMatch.Groups[1].Value : "Main";

            string javaPath = Path.Combine(tempDir, $"{className}.java");
            await File.WriteAllTextAsync(javaPath, code);

            string javacExe = FindExecutable("javac") ?? "javac";
            string compileResult = await RunProcessAsync(javacExe, $"\"{javaPath}\"", tempDir);

            if (compileResult.Contains("error", StringComparison.OrdinalIgnoreCase))
                return $"Compilation errors:\n{compileResult}";

            string javaExe = FindExecutable("java") ?? "java";
            string runResult = await RunProcessAsync(javaExe, $"-cp \"{tempDir}\" {className}", tempDir);
            return string.IsNullOrWhiteSpace(compileResult)
                ? runResult
                : $"Compile output:\n{compileResult}\n\nRun output:\n{runResult}";
        }

        private static string? FindExecutable(string name)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string ext = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

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

        private static async Task<string> RunProcessAsync(string fileName, string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = await Task.Run(() => process.WaitForExit(TimeoutMs));
                if (!exited)
                {
                    try { process.Kill(true); } catch { /* best effort */ }
                    return $"Execution timed out ({TimeoutMs / 1000}s limit).";
                }
            }
            catch (Exception ex)
            {
                return $"Failed to start {fileName}: {ex.Message}";
            }

            string result = output.ToString();
            string err = error.ToString();

            if (!string.IsNullOrWhiteSpace(err))
                result += (string.IsNullOrWhiteSpace(result) ? string.Empty : "\n") + "stderr:\n" + err;

            return string.IsNullOrWhiteSpace(result) ? "(no output)" : result.Trim();
        }
    }
}
