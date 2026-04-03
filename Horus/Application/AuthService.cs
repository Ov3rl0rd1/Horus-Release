using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class AuthService : IAuthService
    {
        public UserInfo? CurrentUser => throw new NotImplementedException();

        public bool IsAuthenticated => throw new NotImplementedException();

        public event EventHandler<AuthStateChangedEventArgs> AuthStateChanged;

        public Task<AuthResult> LoginAsync(string email, string password)
        {
            throw new NotImplementedException();
        }

        public Task LogoutAsync()
        {
            throw new NotImplementedException();
        }

        public Task<AuthResult> RefreshTokenAsync()
        {
            throw new NotImplementedException();
        }
    }
}
