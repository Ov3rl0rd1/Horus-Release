using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class GeoDataService : IGeoDataService
    {
        public bool IsGeoIpLoaded => throw new NotImplementedException();

        public bool IsGeoSiteLoaded => throw new NotImplementedException();

        public DateTime? GeoIpLastUpdated => throw new NotImplementedException();

        public DateTime? GeoSiteLastUpdated => throw new NotImplementedException();

        public event EventHandler<GeoDataUpdatedEventArgs> GeoDataUpdated;

        public Task LoadGeoIpAsync(string path)
        {
            throw new NotImplementedException();
        }

        public Task LoadGeoSiteAsync(string path)
        {
            throw new NotImplementedException();
        }

        public Task<GeoMatchResult> MatchDomainAsync(string domain)
        {
            throw new NotImplementedException();
        }

        public Task<GeoMatchResult> MatchIpAsync(string ip)
        {
            throw new NotImplementedException();
        }

        public Task UpdateGeoDataAsync(string geoIpUrl, string geoSiteUrl)
        {
            throw new NotImplementedException();
        }
    }
}
