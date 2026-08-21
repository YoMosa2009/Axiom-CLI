using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
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
    private const string BrandedRuntimeDirectoryName = "axiom-code";
    private const string BrandedRuntimeVersionFileName = ".axiom-code-version";
    private const string BrandedRuntimeAssetPrefix = "axiom-code-runtime-";
    private static readonly HttpClient BrandedRuntimeHttp = CreateBrandedRuntimeHttpClient();

    private static string ManagedRuntimeRoot => Path.Combine(AppPaths.Root, "OpenCode", "runtime");
    private static string BrandedRuntimeRoot => Path.Combine(ManagedRuntimeRoot, BrandedRuntimeDirectoryName);
    private static string BrandedRuntimeVersionPath => Path.Combine(BrandedRuntimeRoot, BrandedRuntimeVersionFileName);

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
        if (!IsManagedPackageCurrent())
        {
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
        }

        if (!TryFindStockManagedRuntime(out _))
        {
            return new RuntimeInstallResult(
                false,
                "npm completed, but Axiom could not find the OpenCode executable it installed. Run 'npm install -g opencode-ai' and set AXIOM_OPENCODE_PATH to the executable if needed.");
        }

        RuntimeInstallResult branding = await InstallBrandedRuntimeAsync(cancellationToken);
        return branding.Success
            ? new RuntimeInstallResult(true, $"Installed Axiom Code runtime based on OpenCode {PinnedRuntimeVersion}.")
            : branding;
    }

    internal static async Task<RuntimeInstallResult> EnsureManagedRuntimeCurrentAsync(CancellationToken cancellationToken)
    {
        if (!TryFindManagedRuntime(out _))
            return new RuntimeInstallResult(true, "No managed OpenCode runtime is installed.");

        if (IsManagedPackageCurrent() && TryFindBrandedRuntime(out _))
            return new RuntimeInstallResult(true, $"The managed Axiom Code runtime ({PinnedRuntimeVersion}) is already current.");

        return await InstallManagedRuntimeAsync(cancellationToken);
    }

    private static bool TryFindManagedRuntime(out string runtimePath)
    {
        if (TryFindBrandedRuntime(out runtimePath))
            return true;

        return TryFindStockManagedRuntime(out runtimePath);
    }

    private static bool TryFindStockManagedRuntime(out string runtimePath)
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

    private static bool IsManagedPackageCurrent()
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

    private static bool TryFindBrandedRuntime(out string runtimePath)
    {
        string executable = Path.Combine(
            BrandedRuntimeRoot,
            OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
        string currentVersion = UpdateCheckService.GetCurrentVersion().ToString(3);

        try
        {
            if (File.Exists(executable)
                && File.Exists(BrandedRuntimeVersionPath)
                && string.Equals(File.ReadAllText(BrandedRuntimeVersionPath).Trim(), currentVersion, StringComparison.Ordinal))
            {
                runtimePath = executable;
                return true;
            }
        }
        catch (IOException)
        {
            // Fall back to the stock runtime; a later update will repair the branded copy.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall back to the stock runtime; a later update will repair the branded copy.
        }

        runtimePath = string.Empty;
        return false;
    }

    private static async Task<RuntimeInstallResult> InstallBrandedRuntimeAsync(CancellationToken cancellationToken)
    {
        string currentVersion = UpdateCheckService.GetCurrentVersion().ToString(3);
        string assetName = GetBrandedRuntimeAssetName();
        string assetUrl = $"https://github.com/YoMosa2009/Axiom-CLI/releases/download/v{currentVersion}/{assetName}";
        string tempRoot = Path.Combine(Path.GetTempPath(), "axiom-code-runtime-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            string archivePath = Path.Combine(tempRoot, assetName);
            await DownloadBrandedRuntimeAsync(assetUrl, archivePath, cancellationToken);
            ZipFile.ExtractToDirectory(archivePath, tempRoot, overwriteFiles: true);

            string executableName = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
            string source = Path.Combine(tempRoot, executableName);
            if (!File.Exists(source))
                return new RuntimeInstallResult(false, $"Axiom Code runtime asset {assetName} did not contain {executableName}.");

            Directory.CreateDirectory(BrandedRuntimeRoot);
            string target = Path.Combine(BrandedRuntimeRoot, executableName);
            string incoming = target + ".incoming";
            File.Copy(source, incoming, overwrite: true);
            File.Move(incoming, target, overwrite: true);
            File.WriteAllText(BrandedRuntimeVersionPath, currentVersion);
            return new RuntimeInstallResult(true, string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new RuntimeInstallResult(false, $"Could not install the Axiom Code runtime: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best-effort only.
            }
            catch (UnauthorizedAccessException)
            {
                // Temp cleanup is best-effort only.
            }
        }
    }

    private static string GetBrandedRuntimeAssetName()
    {
        string platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "arm64"
            : "x64";
        return $"{BrandedRuntimeAssetPrefix}{platform}-{architecture}.zip";
    }

    private static HttpClient CreateBrandedRuntimeHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("axiom-code-runtime-installer");
        return client;
    }

    private static async Task DownloadBrandedRuntimeAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await BrandedRuntimeHttp.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
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
