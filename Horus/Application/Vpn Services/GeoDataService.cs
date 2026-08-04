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
        private readonly string _geoDbPath;
        private readonly string _geoVersionPath;

        private Reader? _reader;
        private readonly SemaphoreSlim _readerLock = new(1, 1);
        private readonly object _metaLock = new();
        private DateTime? _geoIpLastUpdated;

        public GeoDataService()
        {
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
        /// Loads the cached GeoIP database if one is present.
        ///
        /// HorusAPI v1 no longer serves <c>/geo/country.mmdb</c>, so there is nothing to
        /// download — a database has to be placed at <see cref="_geoDbPath"/> out of band.
        /// Country-based routing on the tunnel itself is handled by xray.
        /// </summary>
        public async Task UpdateGeoDataAsync(string geoIpUrl, string geoSiteUrl)
        {
            if (!File.Exists(_geoDbPath))
                throw new NotSupportedException(
                    "GeoIP database updates are not available — the API no longer serves one.");

            await LoadGeoIpAsync(_geoDbPath);
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
