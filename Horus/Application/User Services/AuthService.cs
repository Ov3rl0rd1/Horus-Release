using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// Owns the signed-in account. Registration no longer produces a session —
    /// the API mails a 6-digit code and <see cref="VerifyEmailAsync"/> exchanges it
    /// for one — so <see cref="CurrentUser"/> is only populated by login, verify or
    /// a restored session.
    /// </summary>
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

        /// <summary>
        /// True while a session token is held. Deliberately independent of the
        /// subscription expiry on <see cref="User"/> — an expired subscription still
        /// leaves the user signed in, it just blocks connecting.
        /// </summary>
        public bool IsAuthenticated => _currentUser != null;

        /// <summary>
        /// Stays <see cref="SubscriptionState.Unknown"/> until <c>/whoami</c> answers, so a
        /// restored session is trusted until the server contradicts it.
        /// </summary>
        public SubscriptionState SubscriptionState { get; private set; } = SubscriptionState.Unknown;

        public event EventHandler<AuthStateChangedEventArgs>? AuthStateChanged;

        public async Task<AuthResult> LoginAsync(string username, string password)
        {
            var result = await _api.LoginAsync(username, password);
            if (!result.Success || result.Response is null)
                return result;

            await _storage.UpdateSessionAsync(result.Response.session, result.Response.expiresAt);
            await SignInAsync(username);

            return result;
        }

        public Task<RegisterResult> RegisterAsync(string username, string email, string password) =>
            _api.RegisterAsync(username, email, password);

        public async Task<AuthResult> VerifyEmailAsync(string email, string code)
        {
            var result = await _api.VerifyEmailAsync(email, code);
            if (!result.Success || result.Response is null)
                return result;

            await _storage.UpdateSessionAsync(result.Response.session, result.Response.expiresAt);
            // The username isn't in the verify response; /whoami fills it in below.
            await SignInAsync(_storage.Username() ?? email);

            return result;
        }

        public Task<RegisterResult> ResendCodeAsync(string email) => _api.ResendCodeAsync(email);

        public Task<RegisterResult> RequestPasswordResetAsync(string email) =>
            _api.RequestPasswordResetAsync(email);

        public async Task LogoutAsync()
        {
            _currentUser = null;
            SubscriptionState = SubscriptionState.Unknown;
            _storage.Clear();
            await Task.CompletedTask;
            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(false, null));
        }

        public async Task<bool> TryRestoreSessionAsync()
        {
            await _storage.Initialization;

            var session = _storage.Session();
            if (string.IsNullOrEmpty(session))
            {
                // Logged because "no session stored" and "startup never ran" look identical
                // from the outside — both leave the user on the sign-in screen.
                System.Diagnostics.Debug.WriteLine(
                    "[Horus] No stored session; routing to sign-in.");
                return false;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[Horus] Restoring session for '{_storage.Username()}' " +
                $"(stored expiry {_storage.SessionExpiresAt()?.ToString("O") ?? "none"}).");

            // Deliberately no local expiry check. A stored token is assumed good and the
            // user goes straight to Home; only the API rejecting it (401, handled globally
            // by HttpAuthHandler) sends them back to Login. A clock skew or a server-side
            // extension would otherwise sign out someone whose session is perfectly valid.
            //
            // Show the cached account immediately so startup isn't gated on the network.
            _currentUser = new User(
                _storage.Username() ?? string.Empty,
                _storage.Subscription(),
                _storage.Email());

            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));

            // Refresh in the background; a rejected session signs the user back out.
            _ = RefreshRestoredSessionAsync();

            return true;
        }

        public async Task<WhoAmIResponse?> RefreshAccountAsync()
        {
            var me = await _api.GetWhoAmIAsync();

            // No answer means offline, not expired — leave the state alone so the UI keeps
            // trusting what it had.
            if (me is null) return null;

            SubscriptionState = me.SubscriptionExpiresAt is { } exp && exp > DateTime.UtcNow
                ? SubscriptionState.Active
                : SubscriptionState.Expired;

            _currentUser = new User(
                string.IsNullOrEmpty(me.Username) ? _currentUser?.username ?? string.Empty : me.Username,
                me.SubscriptionExpiresAt,
                me.Email,
                me.EmailVerified);

            await _storage.UpdateAccountAsync(
                _currentUser.username, _currentUser.email, _currentUser.expiresAt);

            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
            return me;
        }

#if DEBUG
        public void DevSignIn(string email)
        {
            var name = string.IsNullOrWhiteSpace(email) ? "dev@horus.local" : email.Trim();
            _currentUser = new User(name, DateTime.UtcNow.AddDays(30), name, true);
            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
        }
#endif

        // ── Private ──────────────────────────────────────────────────────────

        /// <summary>
        /// Establishes <see cref="CurrentUser"/> after a session is obtained, preferring
        /// the authoritative <c>/whoami</c> snapshot and falling back to what we know
        /// locally when the call doesn't come back.
        /// </summary>
        private async Task SignInAsync(string fallbackUsername)
        {
            _currentUser = new User(fallbackUsername, _storage.Subscription(), _storage.Email());

            var me = await RefreshAccountAsync();
            if (me is not null) return;

            await _storage.UpdateAccountAsync(
                _currentUser.username, _currentUser.email, _currentUser.expiresAt);
            AuthStateChanged?.Invoke(this, new AuthStateChangedEventArgs(true, _currentUser));
        }

        private async Task RefreshRestoredSessionAsync()
        {
            try { await RefreshAccountAsync(); }
            catch (UnauthorizedAccessException) { await LogoutAsync(); }
            catch { /* offline — keep the cached account */ }
        }
    }
}
