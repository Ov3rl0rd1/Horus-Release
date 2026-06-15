using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Application
{
    /// <summary>
    /// Downloads Hysteria2 (and other supported binaries) from their official
    /// GitHub Releases page and installs them into the app's local data directory.
    /// </summary>
    public class BinaryUpdaterService : IBinaryUpdaterService
    {
        private static readonly HttpClient _httpClient = new();

        // Map binary name → GitHub repo + asset name pattern
        private static readonly Dictionary<string, BinarySource> Sources = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hysteria2"] = new BinarySource(
                "apernet/hysteria",
                GetHysteria2AssetName),
        };

        private readonly string _installDir;

        public BinaryUpdaterService()
        {
            _installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Horus", "bin");
            Directory.CreateDirectory(_installDir);

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Horus-VPN/1.0");
        }

        public string GetInstalledBinaryPath(string binaryName)
        {
            var exeName = GetBinaryFileName(binaryName);
            var path = Path.Combine(_installDir, exeName);
            return File.Exists(path) ? path : string.Empty;
        }

        public async Task<BinaryUpdateInfo> CheckForUpdateAsync(string binaryName, CancellationToken ct = default)
        {
            if (!Sources.TryGetValue(binaryName, out var source))
                throw new ArgumentException($"Unknown binary: {binaryName}");

            var release = await FetchLatestReleaseAsync(source.Repo, ct);
            var assetName = source.GetAssetName();
            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.Contains(assetName, StringComparison.OrdinalIgnoreCase));

            if (asset == null)
                throw new InvalidOperationException(
                    $"No asset matching '{assetName}' found in latest release {release.TagName}.");

            var installedPath = GetInstalledBinaryPath(binaryName);
            var currentVersion = ReadInstalledVersion(binaryName);

            return new BinaryUpdateInfo
            {
                BinaryName = binaryName,
                CurrentVersion = currentVersion,
                LatestVersion = release.TagName,
                UpdateAvailable = currentVersion != release.TagName,
                DownloadUrl = asset.BrowserDownloadUrl,
                FileSizeBytes = asset.Size
            };
        }

        public async Task<string> DownloadAndInstallAsync(
            BinaryUpdateInfo update,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            var tempZip = Path.Combine(Path.GetTempPath(), $"horus_bin_{Guid.NewGuid():N}.zip");
            var tempDir = Path.Combine(Path.GetTempPath(), $"horus_bin_{Guid.NewGuid():N}");

            try
            {
                // Download with progress
                using var response = await _httpClient.GetAsync(
                    update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? update.FileSizeBytes;
                await using (var fs = File.Create(tempZip))
                {
                    await using var downloadStream = await response.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await downloadStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded += read;
                        if (totalBytes > 0)
                            progress?.Report((double)downloaded / totalBytes);
                    }
                }

                // Extract
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

                // Find the binary executable inside the archive
                var exePattern = GetBinaryFileName(update.BinaryName);
                var extracted = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(exePattern, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException($"Executable '{exePattern}' not found in archive.");

                var destPath = Path.Combine(_installDir, exePattern);
                File.Copy(extracted, destPath, overwrite: true);

#if !WINDOWS
                // Make executable on Unix-like systems
                try { File.SetUnixFileMode(destPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* Ignore on platforms that don't support it */ }
#endif

                WriteInstalledVersion(update.BinaryName, update.LatestVersion);
                return destPath;
            }
            finally
            {
                try { File.Delete(tempZip); } catch { }
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private async Task<GitHubRelease> FetchLatestReleaseAsync(string repo, CancellationToken ct)
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOpts, ct)
                ?? throw new InvalidOperationException("Failed to parse GitHub release.");
        }

        private static string GetHysteria2AssetName()
        {
#if ANDROID
            var abi = GetAndroidAbi();
            return $"hysteria-android-{abi}";
#elif IOS || MACCATALYST
            return "hysteria-darwin-arm64";
#elif WINDOWS
            return "hysteria-windows-amd64.exe";
#else
            return "hysteria-linux-amd64";
#endif
        }

#if ANDROID
        private static string GetAndroidAbi()
        {
            var abi = Android.OS.Build.CpuAbi ?? string.Empty;
            return abi.StartsWith("arm64") ? "arm64" : abi.StartsWith("armeabi") ? "arm" : "amd64";
        }
#endif

        private static string GetBinaryFileName(string binaryName)
        {
#if WINDOWS
            return binaryName == "hysteria2" ? "hysteria.exe" : binaryName + ".exe";
#elif ANDROID
            return binaryName == "hysteria2" ? "hysteria.so" : binaryName + ".so";
#else
            return binaryName == "hysteria2" ? "hysteria" : binaryName;
#endif
        }

        private string VersionFilePath(string binaryName) =>
            Path.Combine(_installDir, $".{binaryName}.version");

        private string? ReadInstalledVersion(string binaryName)
        {
            var path = VersionFilePath(binaryName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        private void WriteInstalledVersion(string binaryName, string version) =>
            File.WriteAllText(VersionFilePath(binaryName), version);

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private record BinarySource(string Repo, Func<string> GetAssetName);

        private class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = string.Empty;

            [JsonPropertyName("assets")]
            public List<GitHubAsset> Assets { get; set; } = [];
        }

        private class GitHubAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("browser_download_url")]
            public string BrowserDownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; set; }
        }
    }
}
