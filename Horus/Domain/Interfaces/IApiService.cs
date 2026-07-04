using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IApiService
    {
        // Auth
        Task<AuthResult> LoginAsync(string username, string password, string? session = null);
        Task<AuthResult> RegisterAsync(string username, string email, string password);

        // Servers
        Task<IReadOnlyList<ServerInfo>?> GetServersAsync();
        Task<string?> GetServerConfigAsync(int serverId);

        // Geo data
        Task<Stream> DownloadGeoDataAsync(CancellationToken ct = default);
        Task<GeoDataVersion> GetGeoDataVersionAsync();

        // Routing rules
        Task<RoutingRulesFile?> GetRoutingRulesAsync(CancellationToken ct = default);

        // Error reporting
        Task<bool> SendErrorReportAsync(ErrorReport report, CancellationToken ct = default);
    }
}
