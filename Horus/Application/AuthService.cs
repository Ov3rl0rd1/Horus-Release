using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class AuthService : IAuthService
    {
        private readonly IApiService _api;
        private readonly IStorageService _storage;
        private readonly ILocalModeService _localMode;

        private UserInfo? _currentUser;

        public AuthService(IApiService api, IStorageService storage, ILocalModeService localMode)
        {
            _api = api;
            _storage = storage;
            _localMode = localMode;

            // When local mode activates, synthesize an authenticated local user
            _localMode.LocalModeChanged += OnLocalModeChanged;
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
            await PersistUserAsync(result);

            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
            return result;
        }

        public async Task<AuthResult> RegisterAsync(string username, string email, string password)
        {
            var result = await _api.RegisterAsync(username, email, password);
            if (!result.Success) return result;

            _currentUser = result.User;
            await PersistUserAsync(result);

            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
            return result;
        }

        public async Task<AuthResult> RefreshTokenAsync()
        {
            // In local mode, refresh is a no-op — token is synthetic
            if (_localMode.IsLocalMode)
                return new AuthResult { Success = true, Token = "local-mode-token", User = _currentUser, Message = "Local mode" };

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

            // First, probe the API to determine mode
            await _localMode.ProbeApiAsync();

            // In local mode, bypass auth entirely
            if (_localMode.IsLocalMode)
            {
                _currentUser = LocalUser();
                AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
                return true;
            }

            var username = _storage.Username();
            var session = _storage.Session();

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(session))
                return false;

            // Optimistically restore so UI shows previous user immediately
            var sub = _storage.Subscription();
            _currentUser = new UserInfo
            {
                Login = username,
                Session = session,
                ValidUntil = sub ?? DateTime.UtcNow.AddMinutes(5)
            };

            var result = await _api.LoginAsync(username, string.Empty, session);

            // ApiService may have switched to local mode during this call
            if (_localMode.IsLocalMode)
            {
                _currentUser = LocalUser(username);
                return true;
            }

            if (!result.Success)
            {
                _currentUser = null;
                return false;
            }

            _currentUser = result.User;
            await _storage.UpdateTokenAsync(result.Token);
            return true;
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void OnLocalModeChanged(object? sender, bool isLocalMode)
        {
            if (isLocalMode && _currentUser == null)
            {
                _currentUser = LocalUser();
                AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
            }
        }

        private static UserInfo LocalUser(string? name = null) => new()
        {
            Login = name ?? "admin",
            Session = "local",
            ValidUntil = DateTime.UtcNow.AddYears(10)
        };

        private async Task PersistUserAsync(AuthResult result)
        {
            if (result.User == null || string.IsNullOrEmpty(result.Token)) return;
            await _storage.UpdateAsync(
                result.Token,
                result.User.Session,
                result.User.Login,
                result.User.ValidUntil);
        }
    }
}
