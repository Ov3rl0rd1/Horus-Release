using System.Net.Http.Headers;
using System.Text.Json;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application.Update
{
    /// <summary>
    /// Reads releases from the GitHub API.
    ///
    /// Pre-releases count — testers are meant to receive them — but drafts never do, since
    /// a draft is an unfinished release whose assets may not exist yet. The newest release
    /// is chosen by parsing the tag rather than trusting list order or GitHub's own
    /// "latest", which excludes pre-releases and would strand every tester on the last
    /// stable build.
    ///
    /// This source is expected to fail for a large share of users: it is reachable only
    /// when the tunnel is already working, or from a network that does not block GitHub.
    /// Failure here is normal and silent, and <see cref="SiteReleaseSource"/> takes over.
    /// </summary>
    public sealed class GitHubReleaseSource : IUpdateSource
    {
        private readonly IHttpClientFactory _http;
        private readonly string? _apiUrl;

        public UpdateOrigin Origin => UpdateOrigin.GitHub;

        public GitHubReleaseSource(IHttpClientFactory http)
        {
            _http = http;
            _apiUrl = GitHubApi.BuildReleasesUrl(AppConfiguration.UpdateReleasesUrl);
        }

        public async Task<UpdateManifest?> FetchLatestAsync(CancellationToken ct)
        {
            if (_apiUrl is null) return null;

            try
            {
                using var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);

                // GitHub rejects requests with no User-Agent outright.
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("Horus", AppConfiguration.AppVersion));
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                using var response = await client.GetAsync(_apiUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                JsonElement? bestRelease = null;
                var bestVersion = AppVersion.Zero;

                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

                    var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    if (!AppVersion.TryParse(tag, out var version)) continue;
                    if (version <= bestVersion) continue;

                    bestVersion = version;
                    bestRelease = release;
                }

                if (bestRelease is not { } chosen) return null;

                var assets = new List<(string Name, string Url)>();
                if (chosen.TryGetProperty("assets", out var assetArray)
                    && assetArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assetArray.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                            assets.Add((name!, url!));
                    }
                }

                if (assets.Count == 0) return null;

                // The checksums travel as an ordinary asset, so fetch and parse them before
                // anything is offered for download. Without them nothing here is verifiable
                // and the plan will refuse to install.
                var sums = await FetchSumsAsync(client, assets, ct).ConfigureAwait(false);

                var tagName = chosen.TryGetProperty("tag_name", out var tn) ? tn.GetString() : bestVersion.ToString();

                return new UpdateManifest(
                    bestVersion,
                    tagName ?? bestVersion.ToString(),
                    UpdateOrigin.GitHub,
                    [.. assets.Select(a => new UpdateAsset(a.Name, a.Url, sums.GetValueOrDefault(a.Name)))]);
            }
            catch
            {
                // Unreachable, blocked, rate-limited, private repo — all the same answer.
                return null;
            }
        }

        private static async Task<IReadOnlyDictionary<string, string>> FetchSumsAsync(
            HttpClient client, List<(string Name, string Url)> assets, CancellationToken ct)
        {
            var sums = assets.FirstOrDefault(a =>
                a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (sums.Url is null) return new Dictionary<string, string>();

            try
            {
                var text = await client.GetStringAsync(sums.Url, ct).ConfigureAwait(false);
                return Sha256Sums.Parse(text);
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
