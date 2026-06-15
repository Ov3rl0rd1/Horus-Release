using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IApiService
    {
        Task<AuthResult> LoginAsync(string username, string password, string? session = null);
        Task<IReadOnlyList<ServerInfo>> GetServersAsync();
        Task<string> GetServerConfigAsync(int serverId);
    }
}
