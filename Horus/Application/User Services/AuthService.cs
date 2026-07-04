using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class AuthService : IAuthService
    {
        private readonly IApiService _api;
        private readonly IStorageService _storage;

        private User? _currentUser;

        public AuthService(IApiService api, IStorageService storage)
        {
            _api = api;
            _storage = storage;
        }

        public User? CurrentUser => _currentUser;

        public bool IsAuthenticated =>
            _currentUser != null && _currentUser.expiresAt > DateTime.UtcNow;

        public event EventHandler<AuthStateChangedEventArgs>? AuthStateChanged;

        public async Task<AuthResult> LoginAsync(string username, string password)
        {
            var result = await _api.LoginAsync(username, password);

            if (!result.Success) 
                return result;

            _currentUser = new User(username, result.Response.expiresAt);
            await PersistUserAsync(result);

            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
            return result;
        }

        public async Task<AuthResult> RegisterAsync(string username, string email, string password)
        {
            var result = await _api.RegisterAsync(username, email, password);

            if (!result.Success) 
                return result;

            _currentUser = new User(username, result.Response.expiresAt);
            await PersistUserAsync(result);

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

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(session))
                return false;

            var sub = _storage.Subscription();
            _currentUser = new User(username, sub);

            return true;
        }

        private async Task PersistUserAsync(AuthResult result)
        {
            if (result.Success == false || result.Response == null 
                || string.IsNullOrEmpty(result.Response.session)
                || _currentUser == null) 
                return;

            await _storage.UpdateAsync(
                result.Response.session,
                _currentUser.username,
                result.Response.expiresAt);
        }
    }
}
