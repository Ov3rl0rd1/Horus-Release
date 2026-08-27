using System.Security.Cryptography;
using Horus.Application.Update;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Protocols;

namespace Horus.Application.Routing
{
    /// <summary>
    /// Fetches and installs the geo rule files, and points the core at them.
    ///
    /// <para>Deliberately small and self-contained: this is scaffolding, and the part most
    /// likely to change is where the files come from. The provider is a base URL in
    /// configuration rather than anything baked in, because the choice of rule set is a
    /// product decision that will be revisited — see <see cref="AppConfiguration.GeoAssetsBaseUrl"/>.</para>
    ///
    /// <para><b>Verified, not trusted.</b> Each file is checked against the <c>.sha256sum</c>
    /// the provider publishes next to it. These are inputs to routing decisions: a corrupted
    /// or substituted geosite file would silently send traffic the wrong way, which is worse
    /// than not having one at all.</para>
    /// </summary>
    public sealed class GeoAssetService : IGeoAssetService
    {
        private const string GeoIpName = "geoip.dat";
        private const string GeoSiteName = "geosite.dat";
        private const string KeyUpdatedAt = "horus.geo.updatedAtUtc";

        /// <summary>
        /// Generous: geosite alone is ~74 MB, and this only ever runs on an unmetered
        /// network where a slow link is still worth waiting out.
        /// </summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);

        private readonly IHttpClientFactory _http;
        private bool _activated;

        public GeoAssetService(IHttpClientFactory http) => _http = http;

        public string? AssetDirectory
        {
            get
            {
                var dir = Path.Combine(FileSystem.AppDataDirectory, "geo");
                return File.Exists(Path.Combine(dir, GeoIpName)) && File.Exists(Path.Combine(dir, GeoSiteName))
                    ? dir
                    : null;
            }
        }

        public bool IsAvailable => _activated && AssetDirectory is not null;

        public DateTimeOffset? LastUpdatedUtc =>
            long.TryParse(Preferences.Get(KeyUpdatedAt, string.Empty), out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : null;

        public bool Activate()
        {
            var dir = AssetDirectory;
            if (dir is null)
            {
                _activated = false;
                return false;
            }

            try
            {
                XrayInterop.SetAssetPath(dir);
                _activated = true;
                Diag.Info("geo", $"asset path set to {dir}");
                return true;
            }
            catch (Exception ex)
            {
                // An older core without the entry point, or a path it will not accept.
                // Routing simply stays geo-free.
                _activated = false;
                Diag.Warn("geo", $"could not set the asset path: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> UpdateAsync(CancellationToken ct = default)
        {
            var root = AppConfiguration.GeoAssetsBaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(root))
            {
                Diag.Info("geo", "no provider configured; geo routing stays off");
                return false;
            }

            var dir = Path.Combine(FileSystem.AppDataDirectory, "geo");
            Directory.CreateDirectory(dir);

            try
            {
                using var client = _http.CreateClient();
                client.Timeout = DownloadTimeout;

                // Both or neither. A geoip that matches a geosite from a different day is
                // not obviously broken, but the two are built together and mixing them
                // reintroduces exactly the inconsistencies the provider resolves.
                foreach (var name in new[] { GeoIpName, GeoSiteName })
                    if (!await FetchVerifiedAsync(client, root, dir, name, ct).ConfigureAwait(false))
                        return false;

                Preferences.Set(KeyUpdatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                Diag.Info("geo", "rule files updated");

                Activate();
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Diag.Warn("geo", $"update failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Downloads one file beside its checksum and only replaces the installed copy once
        /// the digest matches. A half-written geosite left in place would be loaded on the
        /// next connect and fail the core's own parse.
        /// </summary>
        private static async Task<bool> FetchVerifiedAsync(
            HttpClient client, string root, string dir, string name, CancellationToken ct)
        {
            var target = Path.Combine(dir, name);
            var partial = target + ".part";

            try
            {
                var expected = await ReadChecksumAsync(client, $"{root}/{name}.sha256sum", ct)
                    .ConfigureAwait(false);

                if (expected is null)
                {
                    Diag.Warn("geo", $"{name}: no published checksum; refusing to install it");
                    return false;
                }

                using (var response = await client
                           .GetAsync($"{root}/{name}", HttpCompletionOption.ResponseHeadersRead, ct)
                           .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Diag.Warn("geo", $"{name}: HTTP {(int)response.StatusCode}");
                        return false;
                    }

                    await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = File.Create(partial);
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }

                await using (var stream = File.OpenRead(partial))
                {
                    var actual = Sha256Sums.ToHex(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        Diag.Warn("geo", $"{name}: checksum mismatch, discarding");
                        return false;
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Diag.Warn("geo", $"{name}: {ex.Message}");
                return false;
            }
            finally
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            }
        }

        /// <summary>
        /// Reads a coreutils-style sidecar: <c>&lt;64 hex&gt;  &lt;filename&gt;</c>. Only the
        /// digest is used — the name in it is the provider's, not necessarily ours.
        /// </summary>
        private static async Task<string?> ReadChecksumAsync(HttpClient client, string url, CancellationToken ct)
        {
            try
            {
                var text = (await client.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
                var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                return first is { Length: 64 } && first.All(Uri.IsHexDigit)
                    ? first.ToLowerInvariant()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public void Clear()
        {
            _activated = false;
            try
            {
                var dir = Path.Combine(FileSystem.AppDataDirectory, "geo");
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                Preferences.Remove(KeyUpdatedAt);
                Diag.Info("geo", "rule files removed");
            }
            catch (Exception ex) { Diag.Warn("geo", $"could not clear: {ex.Message}"); }
        }
    }
}
