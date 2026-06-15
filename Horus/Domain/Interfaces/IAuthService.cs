using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IAuthService
    {
        UserInfo? CurrentUser { get; }
        bool IsAuthenticated { get; }

        Task<AuthResult> LoginAsync(string email, string password);
        Task<AuthResult> RegisterAsync(string username, string email, string password);
        Task<AuthResult> RefreshTokenAsync();
        Task LogoutAsync();

        /// <summary>Called on app startup to restore persisted session.</summary>
        Task<bool> TryRestoreSessionAsync();

        event EventHandler<AuthStateChangedEventArgs> AuthStateChanged;
    }
}
