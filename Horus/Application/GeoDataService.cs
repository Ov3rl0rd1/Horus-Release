using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class GeoDataService : IGeoDataService
    {
        public bool IsGeoIpLoaded => false;
        public bool IsGeoSiteLoaded => false;
        public DateTime? GeoIpLastUpdated => null;
        public DateTime? GeoSiteLastUpdated => null;

        public event EventHandler<GeoDataUpdatedEventArgs>? GeoDataUpdated;

        public Task LoadGeoIpAsync(string path) => Task.CompletedTask;
        public Task LoadGeoSiteAsync(string path) => Task.CompletedTask;
        public Task UpdateGeoDataAsync(string geoIpUrl, string geoSiteUrl) => Task.CompletedTask;

        public Task<GeoMatchResult> MatchIpAsync(string ip) =>
            Task.FromResult(new GeoMatchResult { HasMatch = false });

        public Task<GeoMatchResult> MatchDomainAsync(string domain) =>
            Task.FromResult(new GeoMatchResult { HasMatch = false });
    }
}
