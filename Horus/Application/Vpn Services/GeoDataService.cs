using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using MaxMind.Db;
using System.Net;
using System.Text.Json;

namespace Horus.Application
{
    /// <summary>
    /// Loads a MaxMind GeoLite2-Country .mmdb file and performs fast
    /// in-memory IP → country lookups. The file is downloaded from the
    /// backend server and cached in local app data.
    /// </summary>
    public class GeoDataService : IGeoDataService
    {
        private readonly IApiService _api;
        private readonly string _geoDbPath;
        private readonly string _geoVersionPath;

        private Reader? _reader;
        private readonly SemaphoreSlim _readerLock = new(1, 1);
        private readonly object _metaLock = new();
        private DateTime? _geoIpLastUpdated;

        public GeoDataService(IApiService api)
        {
            _api = api;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Horus", "geo");
            Directory.CreateDirectory(dir);
            _geoDbPath = Path.Combine(dir, "country.mmdb");
            _geoVersionPath = Path.Combine(dir, "country.version.json");
        }

        public bool IsGeoIpLoaded => _reader != null;
        public bool IsGeoSiteLoaded => false; // GeoSite uses separate domain list, not mmdb

        public DateTime? GeoIpLastUpdated
        {
            get { lock (_metaLock) return _geoIpLastUpdated; }
        }

        public DateTime? GeoSiteLastUpdated => null;

        public event EventHandler<GeoDataUpdatedEventArgs>? GeoDataUpdated;

        public async Task LoadGeoIpAsync(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("GeoIP database not found.", path);

            await _readerLock.WaitAsync();
            try
            {
                _reader?.Dispose();
                _reader = new Reader(path);
                lock (_metaLock)
                    _geoIpLastUpdated = File.GetLastWriteTimeUtc(path);
            }
            finally
            {
                _readerLock.Release();
            }

            GeoDataUpdated?.Invoke(this, new GeoDataUpdatedEventArgs("geoip", DateTime.UtcNow));
        }

        public Task LoadGeoSiteAsync(string path) => Task.CompletedTask;

        /// <summary>
        /// Downloads the latest GeoIP database from the server.
        /// Only downloads if the server version is newer than the cached version.
        /// </summary>
        public async Task UpdateGeoDataAsync(string geoIpUrl, string geoSiteUrl)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                // Check if update is needed
                var serverVersion = await _api.GetGeoDataVersionAsync();
                var cachedVersion = ReadCachedVersion();

                bool needsUpdate = cachedVersion == null
                    || serverVersion.UpdatedAt > cachedVersion.UpdatedAt
                    || !File.Exists(_geoDbPath);

                if (!needsUpdate) return;

                // Download
                await using var stream = await _api.DownloadGeoDataAsync(cts.Token);
                var tempPath = _geoDbPath + ".tmp";
                await using (var fs = File.Create(tempPath))
                    await stream.CopyToAsync(fs, cts.Token);

                // Validate by opening it
                using (var testReader = new Reader(tempPath)) { /* validates */ }

                // Atomic replace
                File.Move(tempPath, _geoDbPath, overwrite: true);
                WriteCachedVersion(serverVersion);

                // Reload in-memory reader
                await LoadGeoIpAsync(_geoDbPath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // If update fails but we have an existing cached copy, load that
                if (File.Exists(_geoDbPath) && _reader == null)
                    await LoadGeoIpAsync(_geoDbPath);
                throw;
            }
        }

        public Task<GeoMatchResult> MatchIpAsync(string ip)
        {
            if (_reader == null)
                return Task.FromResult(new GeoMatchResult { HasMatch = false });

            try
            {
                if (!IPAddress.TryParse(ip, out var addr))
                    return Task.FromResult(new GeoMatchResult { HasMatch = false });

                var record = _reader.Find<Dictionary<string, object>>(addr);
                if (record == null)
                    return Task.FromResult(new GeoMatchResult { HasMatch = false });

                var country = ExtractCountryIso(record);
                return Task.FromResult(new GeoMatchResult
                {
                    HasMatch = country != null,
                    GeoFile = country
                });
            }
            catch
            {
                return Task.FromResult(new GeoMatchResult { HasMatch = false });
            }
        }

        public Task<GeoMatchResult> MatchDomainAsync(string domain) =>
            Task.FromResult(new GeoMatchResult { HasMatch = false });

        // ── Helpers ─────────────────────────────────────────────────────────

        private static string? ExtractCountryIso(Dictionary<string, object> record)
        {
            if (record.TryGetValue("country", out var countryObj) &&
                countryObj is Dictionary<string, object> countryDict &&
                countryDict.TryGetValue("iso_code", out var iso))
                return iso?.ToString();
            return null;
        }

        private GeoDataVersion? ReadCachedVersion()
        {
            if (!File.Exists(_geoVersionPath)) return null;
            try
            {
                var json = File.ReadAllText(_geoVersionPath);
                return JsonSerializer.Deserialize<GeoDataVersion>(json);
            }
            catch { return null; }
        }

        private void WriteCachedVersion(GeoDataVersion version)
        {
            var json = JsonSerializer.Serialize(version);
            File.WriteAllText(_geoVersionPath, json);
        }
    }
}
