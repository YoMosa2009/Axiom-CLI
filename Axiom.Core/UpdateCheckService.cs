using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core
{
    public sealed class UpdateCheckResult
    {
        public string LatestVersionTag { get; init; } = string.Empty;
        public Version LatestVersion { get; init; } = new(0, 0, 0, 0);
        public Version CurrentVersion { get; init; } = new(0, 0, 0, 0);
        public string ReleasePageUrl { get; init; } = string.Empty;
        public string AssetDownloadUrl { get; init; } = string.Empty;
        public string AssetFileName { get; init; } = string.Empty;
        public bool IsNewerVersionAvailable { get; init; }
        public bool HasAsset => !string.IsNullOrWhiteSpace(AssetDownloadUrl);
    }

    // Adapted from the WPF app's UpdateCheckService (same GitHub-releases-API pattern, already
    // portable — plain HttpClient + System.Text.Json, no WPF dependency) — pointed at the CLI's
    // own repo, and matching release assets by platform+arch instead of a single Windows
    // installer, since this ships one archive per OS/architecture (see release.yml).
    public static class UpdateCheckService
    {
        private const string RepoSlug = "YoMosa2009/Axiom-CLI";
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/" + RepoSlug + "/releases/latest";
        private static readonly Regex VersionInTagRegex = new(@"(?<version>\d+(?:\.\d+){1,3})", RegexOptions.Compiled);
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("axiom-cli-update-check");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static Version GetCurrentVersion()
            => NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

        // Matches the archive names release.yml produces: axiom-cli-{win|linux|osx}-{x64|arm64}.{zip|tar.gz}
        public static string GetCurrentPlatformAssetName()
        {
            string platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string extension = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
            return $"axiom-cli-{platform}-{arch}.{extension}";
        }

        public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken token = default)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(LatestReleaseApiUrl, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null; // 404 also covers "no releases yet" — not worth surfacing as an error.

                string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(tag) || !TryParseVersionTag(tag, out Version latestVersion))
                    return null;

                string releaseUrl = root.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                (string downloadUrl, string fileName) = FindPlatformAsset(root);
                Version currentVersion = GetCurrentVersion();

                return new UpdateCheckResult
                {
                    LatestVersionTag = tag.Trim(),
                    LatestVersion = latestVersion,
                    CurrentVersion = currentVersion,
                    ReleasePageUrl = releaseUrl,
                    AssetDownloadUrl = downloadUrl,
                    AssetFileName = fileName,
                    IsNewerVersionAvailable = latestVersion > currentVersion
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await BackendLogService.LogErrorAsync("UpdateCheck", ex).ConfigureAwait(false);
                return null; // Update checks must never break a chat/code turn.
            }
        }

        public static async Task<string> DownloadAssetAsync(string downloadUrl, string fileName, CancellationToken token)
        {
            string safeName = string.IsNullOrWhiteSpace(fileName) ? "axiom-cli-update" : Path.GetFileName(fileName.Trim());
            string targetPath = Path.Combine(Path.GetTempPath(), safeName);

            using HttpResponseMessage response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(target, token).ConfigureAwait(false);

            return targetPath;
        }

        private static bool TryParseVersionTag(string tag, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            Match match = VersionInTagRegex.Match(tag ?? string.Empty);
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out Version? parsed))
                return false;

            version = NormalizeVersion(parsed);
            return true;
        }

        // Version treats missing components as -1, so "0.1" would compare *below* "0.1.0.0".
        private static Version NormalizeVersion(Version version)
            => new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

        private static (string Url, string Name) FindPlatformAsset(JsonElement root)
        {
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return (string.Empty, string.Empty);

            string wantedName = GetCurrentPlatformAssetName();
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                if (!string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(url))
                    return (url, name);
            }

            return (string.Empty, string.Empty);
        }
    }
}
