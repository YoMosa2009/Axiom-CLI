using System.Diagnostics;
using System.Text.Json;
using Axiom.Core;
using Axiom.Core.OpenCode;

namespace Axiom.Cli;

internal static class OpenCodeRunner
{
    internal const string RuntimePathEnvironmentVariable = "AXIOM_OPENCODE_PATH";
    internal const string NpmPackageName = "opencode-ai";
    // Updating OpenCode is a compatibility decision, not an implicit behavior change for users.
    internal const string PinnedRuntimeVersion = "1.18.18";

    private static string ManagedRuntimeRoot => Path.Combine(AppPaths.Root, "OpenCode", "runtime");

    internal sealed record RuntimeInstallResult(bool Success, string Message);

    internal static bool TryFindRuntime(out string runtimePath)
    {
        string? configured = Environment.GetEnvironmentVariable(RuntimePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            runtimePath = configured.Trim();
            return true;
        }

        if (TryFindManagedRuntime(out runtimePath))
            return true;

        string[] names = OperatingSystem.IsWindows()
            ? ["opencode.exe", "opencode.cmd", "opencode.bat", "opencode"]
            : ["opencode"];

        foreach (string segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(segment, name);
                if (File.Exists(candidate))
                {
                    runtimePath = candidate;
                    return true;
                }
            }
        }

        runtimePath = string.Empty;
        return false;
    }

    internal static async Task<RuntimeInstallResult> InstallManagedRuntimeAsync(CancellationToken cancellationToken)
    {
        if (TryFindManagedRuntime(out _) && IsManagedRuntimeCurrent())
            return new RuntimeInstallResult(true, $"The managed OpenCode runtime ({PinnedRuntimeVersion}) is already installed.");

        if (!TryFindExecutable("npm", out string npmPath))
        {
            return new RuntimeInstallResult(
                false,
                "Node.js (including npm) is required for the managed OpenCode install. Install the current Node.js LTS release, then run 'axiom opencode install' again.");
        }

        Directory.CreateDirectory(ManagedRuntimeRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = npmPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ManagedRuntimeRoot
        };
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--prefix");
        startInfo.ArgumentList.Add(ManagedRuntimeRoot);
        startInfo.ArgumentList.Add("--no-audit");
        startInfo.ArgumentList.Add("--no-fund");
        startInfo.ArgumentList.Add($"{NpmPackageName}@{PinnedRuntimeVersion}");

        using Process? process = Process.Start(startInfo);
        if (process == null)
            return new RuntimeInstallResult(false, "npm could not be started.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = (await stdoutTask).Trim();
        string error = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? output : error;
            return new RuntimeInstallResult(
                false,
                string.IsNullOrWhiteSpace(detail)
                    ? $"npm failed while installing {NpmPackageName}@{PinnedRuntimeVersion} (exit code {process.ExitCode})."
                    : $"npm failed while installing OpenCode: {detail}");
        }

        if (!TryFindManagedRuntime(out _))
        {
            return new RuntimeInstallResult(
                false,
                "npm completed, but Axiom could not find the OpenCode executable it installed. Run 'npm install -g opencode-ai' and set AXIOM_OPENCODE_PATH to the executable if needed.");
        }

        return new RuntimeInstallResult(true, $"Installed managed OpenCode runtime {PinnedRuntimeVersion}.");
    }

    internal static async Task<RuntimeInstallResult> EnsureManagedRuntimeCurrentAsync(CancellationToken cancellationToken)
    {
        if (!TryFindManagedRuntime(out _))
            return new RuntimeInstallResult(true, "No managed OpenCode runtime is installed.");

        if (IsManagedRuntimeCurrent())
            return new RuntimeInstallResult(true, $"The managed OpenCode runtime ({PinnedRuntimeVersion}) is already current.");

        return await InstallManagedRuntimeAsync(cancellationToken);
    }

    private static bool TryFindManagedRuntime(out string runtimePath)
    {
        string[] relativeCandidates = OperatingSystem.IsWindows()
            ? ["node_modules", ".bin", "opencode.cmd"]
            : ["node_modules", ".bin", "opencode"];
        string candidate = Path.Combine([ManagedRuntimeRoot, .. relativeCandidates]);
        if (File.Exists(candidate))
        {
            runtimePath = candidate;
            return true;
        }

        runtimePath = string.Empty;
        return false;
    }

    private static bool IsManagedRuntimeCurrent()
    {
        string manifestPath = Path.Combine(ManagedRuntimeRoot, "node_modules", NpmPackageName, "package.json");
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return manifest.RootElement.TryGetProperty("version", out JsonElement version)
                && string.Equals(version.GetString(), PinnedRuntimeVersion, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryFindExecutable(string executableName, out string executablePath)
    {
        string[] names = OperatingSystem.IsWindows()
            ? [$"{executableName}.cmd", $"{executableName}.exe", $"{executableName}.bat", executableName]
            : [executableName];

        foreach (string segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(segment, name);
                if (File.Exists(candidate))
                {
                    executablePath = candidate;
                    return true;
                }
            }
        }

        executablePath = string.Empty;
        return false;
    }

    internal static async Task<int> RunAsync(
        string runtimePath,
        string baseUrl,
        string apiKey,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!KestrelOpenCodeConfiguration.TryCreate(baseUrl, autoApprove: arguments.Contains("--auto"), out string config, out string error))
            throw new InvalidOperationException(error);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No Kestrel access key is configured. Run 'axiom connect'.");

        string isolatedRoot = Path.Combine(AppPaths.Root, "OpenCode");
        Directory.CreateDirectory(isolatedRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = runtimePath,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // Keep OpenCode's data/config isolated from an existing standalone OpenCode install.
        // The credential only exists in this child process and is not written by Axiom.
        startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(isolatedRoot, "config");
        startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(isolatedRoot, "data");
        startInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(isolatedRoot, "cache");
        startInfo.Environment["OPENCODE_CONFIG_DIR"] = Path.Combine(isolatedRoot, "extensions");
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"] = config;
        startInfo.Environment[KestrelOpenCodeConfiguration.ApiKeyEnvironmentVariable] = apiKey.Trim();

        using Process? process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("OpenCode could not be started.");

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
