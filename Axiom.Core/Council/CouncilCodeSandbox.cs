using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Tools;

namespace Axiom.Core.Council
{
    // Executes Builder code in the same sandboxes the desktop council uses (Python via
    // Python.Runtime, Java via javac/java) so Critic receives real runtime evidence.
    public sealed class CouncilCodeSandbox
    {
        private readonly PythonExecutionService _python;

        public CouncilCodeSandbox(PythonExecutionService? python = null)
        {
            _python = python ?? new PythonExecutionService();
        }

        public async Task<CouncilSandboxResult> ExecuteAsync(
            string builderOutput,
            string language,
            CancellationToken cancellationToken = default)
        {
            string code = StaticValidation.ExtractCodeBlock(builderOutput, language);
            if (string.IsNullOrWhiteSpace(code))
                return CouncilSandboxResult.Empty;

            try
            {
                return language.ToLowerInvariant() switch
                {
                    "python" => await RunPythonAsync(code, cancellationToken).ConfigureAwait(false),
                    "java" => await RunJavaAsync(code).ConfigureAwait(false),
                    _ => CouncilSandboxResult.Unsupported(language)
                };
            }
            catch (Exception ex)
            {
                return new CouncilSandboxResult(
                    Language: language,
                    Output: $"Sandbox error: {ex.Message}",
                    TimedOut: false,
                    Succeeded: false);
            }
        }

        private async Task<CouncilSandboxResult> RunPythonAsync(string code, CancellationToken cancellationToken)
        {
            PythonExecutionResult result = await _python
                .ExecuteMathScriptAsync(code, timeoutMs: 10_000, token: cancellationToken)
                .ConfigureAwait(false);

            if (result.TimedOut)
            {
                return new CouncilSandboxResult(
                    "python",
                    "[[PYTHON TIMEOUT]] Python execution timed out after 10s.",
                    TimedOut: true,
                    Succeeded: false);
            }

            string output = string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output.Trim();
            return new CouncilSandboxResult("python", output, TimedOut: false, Succeeded: result.Success);
        }

        private static async Task<CouncilSandboxResult> RunJavaAsync(string code)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AxiomSandbox", Guid.NewGuid().ToString("N"));
            try
            {
                string output = await JavaSandboxService.RunAsync(code, tempDir).ConfigureAwait(false);
                bool failed = output.Contains("Compilation errors", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("error:", StringComparison.OrdinalIgnoreCase);
                return new CouncilSandboxResult("java", output, TimedOut: false, Succeeded: !failed);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }

    public sealed record CouncilSandboxResult(
        string Language,
        string Output,
        bool TimedOut,
        bool Succeeded)
    {
        public static CouncilSandboxResult Empty { get; } =
            new(string.Empty, string.Empty, false, true);

        public static CouncilSandboxResult Unsupported(string language) =>
            new(language, $"Unsupported sandbox language: {language}", false, false);

        public bool HasOutput => !string.IsNullOrWhiteSpace(Output);
    }
}
