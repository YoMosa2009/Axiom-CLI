using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core;

namespace Axiom.Cli;

// Downloads and applies an axiom-cli release over the currently running installation. The
// running process still has its old code in memory regardless (you cannot replace your own
// loaded image), so this always ends with "restart axiom" rather than an in-place restart.
internal static class UpdateInstaller
{
    public static async Task<(bool Success, string Message)> ApplyLatestAsync(CancellationToken token)
    {
        UpdateCheckResult? update = await UpdateCheckService.CheckForUpdateAsync(token);
        if (update == null)
            return (false, "Could not reach GitHub to check for updates.");

        if (!update.IsNewerVersionAvailable)
            return (true, $"Already up to date (v{update.CurrentVersion}).");

        if (!update.HasAsset)
            return (false,
                $"A newer version ({update.LatestVersionTag}) is available, but no release asset was found " +
                $"for this platform ({UpdateCheckService.GetCurrentPlatformAssetName()}). " +
                $"See {update.ReleasePageUrl}");

        string archivePath = await UpdateCheckService.DownloadAssetAsync(update.AssetDownloadUrl, update.AssetFileName, token);
        string extractDir = Path.Combine(Path.GetTempPath(), "axiom-cli-update-" + Guid.NewGuid());
        Directory.CreateDirectory(extractDir);

        try
        {
            Extract(archivePath, extractDir);

            string? installDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(installDir))
                return (false, "Could not determine the current installation directory.");

            foreach (string sourceFile in Directory.GetFiles(extractDir))
            {
                string destFile = Path.Combine(installDir, Path.GetFileName(sourceFile));
                ReplaceFile(sourceFile, destFile);
            }

            return (true,
                $"Updated to {update.LatestVersionTag}. Restart axiom to use the new version.");
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(extractDir);
        }
    }

    // Cleans up ".old" backup files left behind by a previous update that couldn't delete its
    // own backup while still running (common on Windows, where a loaded file can be renamed
    // away but not deleted until every handle to it closes).
    public static void CleanupPendingBackups()
    {
        string? installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return;

        foreach (string oldFile in Directory.GetFiles(installDir, "*.old"))
            TryDelete(oldFile);
    }

    private static void Extract(string archivePath, string destinationDir)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
            return;
        }

        using FileStream fileStream = File.OpenRead(archivePath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destinationDir, overwriteFiles: true);
    }

    // A running executable's own backing file can usually be renamed (even while executing) but
    // not deleted until the process exits — on any OS. Overwriting in place is not guaranteed to
    // be safe if the OS has the file memory-mapped, so this always renames-out, moves-in.
    private static void ReplaceFile(string sourcePath, string destPath)
    {
        if (File.Exists(destPath))
        {
            string backupPath = destPath + ".old";
            TryDelete(backupPath); // leftover from a previous update
            File.Move(destPath, backupPath);
        }

        File.Move(sourcePath, destPath);

        if (!OperatingSystem.IsWindows())
            TrySetExecutable(destPath);
    }

    private static void TrySetExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best effort — if this fails the user can chmod +x manually.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort, see CleanupPendingBackups */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
