using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IAuthService
    {
        UserInfo? CurrentUser { get; }
        bool IsAuthenticated { get; }

        Task<AuthResult> LoginAsync(string email, string password);
        Task<AuthResult> RefreshTokenAsync();
        Task LogoutAsync();

        event EventHandler<AuthStateChangedEventArgs> AuthStateChanged;
    }
}
