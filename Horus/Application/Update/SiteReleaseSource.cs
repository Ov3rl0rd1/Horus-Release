using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application.Update
{
    /// <summary>
    /// The fallback for everyone GitHub is unreachable for — which, for this product's
    /// users, is most of them when the tunnel is down.
    ///
    /// The site serves the payloads under fixed names with no version in them, so there is
    /// nothing to read a version <i>from</i> except <c>SHA256SUMS.txt</c>, whose entries do
    /// carry it (<c>Horus-0.1.0-win-x64.msi</c>). One request therefore answers both
    /// questions: what the latest version is, and what each file should hash to. See
    /// <see cref="Sha256Sums"/> for why that makes the artifact naming a contract.
    /// </summary>
    public sealed class SiteReleaseSource : IUpdateSource
    {
        private const string FILES_ROUTE = "/download";

        /// <summary>
        /// What the site publishes. The names are fixed by the host, and each maps onto the
        /// versioned entry in the checksum file by suffix.
        /// </summary>
        private static readonly string[] FileNames =
        [
            "Horus-win-x64.msi",
            "Horus-win-x64-portable.zip",
            "Horus-android-arm64-v8a.apk",
            "Horus-android-x86_64.apk"
        ];

        private readonly IHttpClientFactory _http;

        public UpdateOrigin Origin => UpdateOrigin.Site;

        public SiteReleaseSource(IHttpClientFactory http) => _http = http;

        public async Task<UpdateManifest?> FetchLatestAsync(CancellationToken ct)
        {
            var root = AppConfiguration.ApiBaseUrl?.TrimEnd('/') + FILES_ROUTE;
            if (string.IsNullOrWhiteSpace(root)) return null;

            try
            {
                using var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);

                var text = await client.GetStringAsync($"{root}/SHA256SUMS.txt", ct).ConfigureAwait(false);
                var sums = Sha256Sums.Parse(text);
                if (sums.Count == 0) return null;

                var version = Sha256Sums.InferVersion(sums.Keys);
                if (version == AppVersion.Zero) return null;

                // Every asset carries the digest of its versioned twin. An entry with no
                // matching digest is dropped rather than offered unverifiable.
                var assets = new List<UpdateAsset>();
                foreach (var name in FileNames)
                {
                    var suffix = name["Horus".Length..];              // "-win-x64.msi"
                    var digest = Sha256Sums.DigestForSuffix(sums, suffix);
                    if (digest is null) continue;
                    assets.Add(new UpdateAsset(name, $"{root}/{name}", digest));
                }

                return assets.Count == 0
                    ? null
                    : new UpdateManifest(version, $"v{version}", UpdateOrigin.Site, assets);
            }
            catch
            {
                return null;
            }
        }
    }
}
