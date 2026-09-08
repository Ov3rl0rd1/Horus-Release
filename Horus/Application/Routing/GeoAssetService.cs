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
    /// <para>The provider is two URLs in configuration rather than anything baked in —
    /// <see cref="AppConfiguration.GeoIpUrl"/> and <see cref="AppConfiguration.GeoSiteUrl"/>.
    /// Two, not one base, because the files come from two repositories
    /// (<c>Ov3rl0rd1/horus-geoip</c> and <c>Ov3rl0rd1/horus-geosite</c>) and there is no
    /// common root to hang them off. Each publishes a <c>release</c> branch whose contents
    /// are exactly the <c>.dat</c> and its <c>.sha256sum</c>, so the raw URLs stay valid
    /// across rebuilds.</para>
    ///
    /// <para><b>Verified, not trusted.</b> Each file is checked against the published
    /// checksum before it replaces the installed copy. These are inputs to routing
    /// decisions: a corrupted or substituted geosite would silently send traffic the wrong
    /// way, which is worse than not having one at all.</para>
    /// </summary>
    public sealed class GeoAssetService : IGeoAssetService
    {
        private const string GeoIpName = "geoip.dat";
        private const string GeoSiteName = "geosite.dat";
        private const string KeyUpdatedAt = "horus.geo.updatedAtUtc";

        /// <summary>Digest of each installed file, so a check need not re-hash it.</summary>
        private static string DigestKey(string name) => $"horus.geo.sha.{name}";

        /// <summary>
        /// The pair is about 400 KB, so this is not sized for the payload — it is sized for
        /// the link. These downloads happen with the tunnel down, over exactly the filtered
        /// path the app exists to get around, where a few KB/s is a normal outcome rather
        /// than a broken one.
        /// </summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

        /// <summary>A checksum sidecar is ~100 bytes; waiting minutes for one proves nothing.</summary>
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

        private readonly IHttpClientFactory _http;
        private bool _activated;

        public GeoAssetService(IHttpClientFactory http) => _http = http;

        /// <summary>
        /// Android only. See <see cref="IGeoAssetService.IsSupported"/> for why Windows is
        /// excluded rather than merely untested — a direct rule there loops back into the
        /// tunnel, and the 23 000 prefixes of <c>geoip:ru</c> cannot each be given the
        /// <c>/32</c> host route that keeps the node and the resolvers out of it.
        /// </summary>
        public bool IsSupported =>
#if ANDROID
            true;
#else
            false;
#endif

        private static string AssetRoot => Path.Combine(FileSystem.AppDataDirectory, "geo");

        public string? AssetDirectory
        {
            get
            {
                var dir = AssetRoot;
                return File.Exists(Path.Combine(dir, GeoIpName)) && File.Exists(Path.Combine(dir, GeoSiteName))
                    ? dir
                    : null;
            }
        }

        public bool IsInstalled => AssetDirectory is not null;

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

        // ── Update check ─────────────────────────────────────────────────────

        public async Task<GeoAssetCheck> CheckAsync(CancellationToken ct = default)
        {
            if (!IsInstalled) return GeoAssetCheck.Missing;

            var installedAt = LastUpdatedUtc;
            var sources = Sources();
            if (sources.Count == 0) return new GeoAssetCheck(GeoAssetState.Unknown, installedAt);

            try
            {
                using var client = _http.CreateClient();
                client.Timeout = CheckTimeout;

                var stale = false;

                foreach (var (name, url) in sources)
                {
                    var published = await ReadChecksumAsync(client, url + ".sha256sum", ct)
                        .ConfigureAwait(false);

                    // One unreachable sidecar makes the whole answer unknown. Reporting
                    // "up to date" on the strength of the other file would be a guess
                    // dressed as a fact.
                    if (published is null)
                        return new GeoAssetCheck(GeoAssetState.Unknown, installedAt);

                    var local = await InstalledDigestAsync(name, ct).ConfigureAwait(false);
                    if (local is null)
                        return new GeoAssetCheck(GeoAssetState.Unknown, installedAt);

                    if (!string.Equals(local, published, StringComparison.OrdinalIgnoreCase))
                        stale = true;
                }

                return new GeoAssetCheck(
                    stale ? GeoAssetState.UpdateAvailable : GeoAssetState.UpToDate, installedAt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Diag.Info("geo", $"update check failed: {ex.Message}");
                return new GeoAssetCheck(GeoAssetState.Unknown, installedAt);
            }
        }

        /// <summary>
        /// The digest of the installed copy: from the note made when it was installed, or —
        /// when that note is gone, which is what a cleared preference store or a restored
        /// backup looks like — by hashing the file. 400 KB, so the fallback is not a cost
        /// worth avoiding; it just should not be the normal path.
        /// </summary>
        private static async Task<string?> InstalledDigestAsync(string name, CancellationToken ct)
        {
            var noted = Preferences.Get(DigestKey(name), string.Empty);
            if (noted.Length == 64) return noted;

            try
            {
                var path = Path.Combine(AssetRoot, name);
                if (!File.Exists(path)) return null;

                await using var stream = File.OpenRead(path);
                var digest = Sha256Sums.ToHex(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                Preferences.Set(DigestKey(name), digest);
                return digest;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // ── Install ──────────────────────────────────────────────────────────

        public async Task<bool> UpdateAsync(CancellationToken ct = default)
        {
            var sources = Sources();
            if (sources.Count == 0)
            {
                Diag.Info("geo", "no provider configured; geo routing stays off");
                return false;
            }

            var dir = AssetRoot;
            Directory.CreateDirectory(dir);

            try
            {
                using var client = _http.CreateClient();
                client.Timeout = DownloadTimeout;

                // Both or neither. The two files are a matched pair by construction —
                // geosite:ru-exclude only means anything against the geoip set built beside
                // it — and half an update is the state where routing looks configured and
                // sends the wrong half of the traffic the wrong way.
                foreach (var (name, url) in sources)
                    if (!await FetchVerifiedAsync(client, dir, name, url, ct).ConfigureAwait(false))
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
        /// The files to fetch, or nothing when the configuration does not name both.
        ///
        /// Either alone is useless: the generated config names both categories, so a
        /// missing geoip file fails the core's parse just as a missing geosite one does.
        /// </summary>
        private static List<(string Name, string Url)> Sources()
        {
            var ip = AppConfiguration.GeoIpUrl?.Trim();
            var site = AppConfiguration.GeoSiteUrl?.Trim();

            return string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(site)
                ? []
                : [(GeoIpName, ip), (GeoSiteName, site)];
        }

        /// <summary>
        /// Downloads one file beside its checksum and only replaces the installed copy once
        /// the digest matches. A half-written geosite left in place would be loaded on the
        /// next connect and fail the core's own parse.
        /// </summary>
        private static async Task<bool> FetchVerifiedAsync(
            HttpClient client, string dir, string name, string url, CancellationToken ct)
        {
            var target = Path.Combine(dir, name);
            var partial = target + ".part";

            try
            {
                var expected = await ReadChecksumAsync(client, url + ".sha256sum", ct)
                    .ConfigureAwait(false);

                if (expected is null)
                {
                    Diag.Warn("geo", $"{name}: no published checksum; refusing to install it");
                    return false;
                }

                using (var response = await client
                           .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
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

                string actual;
                await using (var stream = File.OpenRead(partial))
                {
                    actual = Sha256Sums.ToHex(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        Diag.Warn("geo", $"{name}: checksum mismatch, discarding");
                        return false;
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target);

                // Noted so the next check costs two sidecar fetches rather than two hashes.
                Preferences.Set(DigestKey(name), actual);
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
        /// Reads a coreutils-style sidecar: a 64-character digest, whitespace, the file
        /// name. Only the digest is used — the name in it is the provider's, not
        /// necessarily ours.
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
                var dir = AssetRoot;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

                Preferences.Remove(KeyUpdatedAt);
                Preferences.Remove(DigestKey(GeoIpName));
                Preferences.Remove(DigestKey(GeoSiteName));
                Diag.Info("geo", "rule files removed");
            }
            catch (Exception ex) { Diag.Warn("geo", $"could not clear: {ex.Message}"); }
        }
    }
}
