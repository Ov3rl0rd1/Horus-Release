using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class AuthService : IAuthService
    {
        private readonly IApiService _api;
        private readonly IStorageService _storage;

        private UserInfo? _currentUser;

        public AuthService(IApiService api, IStorageService storage)
        {
            _api = api;
            _storage = storage;
        }

        public UserInfo? CurrentUser => _currentUser;

        public bool IsAuthenticated =>
            _currentUser != null && _currentUser.ValidUntil > DateTime.UtcNow;

        public event EventHandler<AuthStateChangedEventArgs>? AuthStateChanged;

        public async Task<AuthResult> LoginAsync(string username, string password)
        {
            var result = await _api.LoginAsync(username, password);
            if (!result.Success) return result;

            _currentUser = result.User;
            await _storage.UpdateAsync(
                result.Token,
                result.User!.Session,
                result.User.Login,
                result.User.ValidUntil);

            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
            return result;
        }

        public async Task<AuthResult> RefreshTokenAsync()
        {
            var username = _storage.Username();
            var session = _storage.Session();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(session))
                return new AuthResult { Success = false, Message = "No stored session." };

            var result = await _api.LoginAsync(username, string.Empty, session);
            if (!result.Success) return result;

            _currentUser = result.User;
            await _storage.UpdateTokenAsync(result.Token);
            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
            return result;
        }

        public async Task LogoutAsync()
        {
            _currentUser = null;
            if (_storage is StorageService concrete)
                concrete.Clear();
            await Task.CompletedTask;
            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(false, null));
        }

        public async Task<bool> TryRestoreSessionAsync()
        {
            await _storage.Initialization;
            var username = _storage.Username();
            var session = _storage.Session();
            var token = _storage.Token();

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(session))
                return false;

            // Optimistically restore from stored data, then refresh token
            var sub = _storage.Subscription();
            _currentUser = new UserInfo
            {
                Login = username,
                Session = session,
                ValidUntil = sub ?? DateTime.UtcNow.AddMinutes(5)
            };

            var result = await _api.LoginAsync(username, string.Empty, session);
            if (!result.Success)
            {
                _currentUser = null;
                return false;
            }

            _currentUser = result.User;
            await _storage.UpdateTokenAsync(result.Token);
            return true;
        }
    }
}
