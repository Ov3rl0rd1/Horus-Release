using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IGeoDataService
    {
        bool IsGeoIpLoaded { get; }
        bool IsGeoSiteLoaded { get; }
        DateTime? GeoIpLastUpdated { get; }
        DateTime? GeoSiteLastUpdated { get; }

        Task LoadGeoIpAsync(string path);
        Task LoadGeoSiteAsync(string path);
        Task<GeoMatchResult> MatchIpAsync(string ip);
        Task<GeoMatchResult> MatchDomainAsync(string domain);
        Task UpdateGeoDataAsync(string geoIpUrl, string geoSiteUrl);

        event EventHandler<GeoDataUpdatedEventArgs> GeoDataUpdated;
    }
}
