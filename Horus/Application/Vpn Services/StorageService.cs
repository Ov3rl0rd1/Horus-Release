using Horus.Domain.Interfaces;
using System.Globalization;

namespace Horus.Application
{
    /// <summary>
    /// Persists the session token and the account snapshot in platform secure storage.
    /// Session expiry and subscription expiry are stored separately — the API reports
    /// them on different endpoints and they are not interchangeable.
    /// </summary>
    public class StorageService : IStorageService
    {
        public Task Initialization { get; }

        private const string SESSION_KEY = "api_session";
        private const string SESSION_EXPIRY_KEY = "api_session_expires_at";
        private const string USERNAME_KEY = "api_username";
        private const string EMAIL_KEY = "api_email";
        private const string SUBSCRIPTION_KEY = "api_subscription_expires_at";

        private string? _session;
        private DateTime? _sessionExpiresAt;
        private string? _username;
        private string? _email;
        private DateTime? _subscription;

        public StorageService()
        {
            Initialization = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _session = await SecureStorage.GetAsync(SESSION_KEY);
                _username = await SecureStorage.GetAsync(USERNAME_KEY);
                _email = await SecureStorage.GetAsync(EMAIL_KEY);
                _sessionExpiresAt = ParseDate(await SecureStorage.GetAsync(SESSION_EXPIRY_KEY));
                _subscription = ParseDate(await SecureStorage.GetAsync(SUBSCRIPTION_KEY));
            }
            catch
            {
                // A corrupt keystore entry must not stop the app from starting —
                // the user simply lands on the login screen.
            }
        }

        public string? Session() => _session;
        public DateTime? SessionExpiresAt() => _sessionExpiresAt;
        public string? Username() => _username;
        public string? Email() => _email;
        public DateTime? Subscription() => _subscription;

        public async Task UpdateSessionAsync(string session, DateTime? sessionExpiresAt = null)
        {
            _session = session;
            _sessionExpiresAt = sessionExpiresAt;

            await WriteAsync(SESSION_KEY, session);
            await WriteDateAsync(SESSION_EXPIRY_KEY, sessionExpiresAt);
        }

        public async Task UpdateAccountAsync(string username, string? email, DateTime? subscription)
        {
            _username = username;
            _email = email;
            _subscription = subscription;

            await WriteAsync(USERNAME_KEY, username);
            await WriteAsync(EMAIL_KEY, email);
            await WriteDateAsync(SUBSCRIPTION_KEY, subscription);
        }

        public async Task UpdateSubscriptionAsync(DateTime? subscription)
        {
            _subscription = subscription;
            await WriteDateAsync(SUBSCRIPTION_KEY, subscription);
        }

        public void Clear()
        {
            _session = null;
            _sessionExpiresAt = null;
            _username = null;
            _email = null;
            _subscription = null;

            // Only our own keys. RemoveAll() wipes the whole app's secure store, which is
            // more than logout should ever do.
            foreach (var key in new[]
                     { SESSION_KEY, SESSION_EXPIRY_KEY, USERNAME_KEY, EMAIL_KEY, SUBSCRIPTION_KEY })
            {
                try { SecureStorage.Remove(key); } catch { /* already absent */ }
            }
        }

        /// <summary>
        /// Stores a value, or clears the key when there is nothing to store.
        ///
        /// An empty string is not a storable value here: on Windows SecureStorage encrypts
        /// through DataProtectionProvider, which rejects a zero-length buffer with
        /// "Value does not fall within the expected range" — so writing "" for an absent
        /// expiry threw during login. Android happened to tolerate it, which is why this
        /// only ever surfaced on desktop.
        /// </summary>
        private static async Task WriteAsync(string key, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                SecureStorage.Remove(key);
                return;
            }

            await SecureStorage.SetAsync(key, value);
        }

        private static Task WriteDateAsync(string key, DateTime? value) =>
            WriteAsync(key, value?.ToUniversalTime().ToString("O"));

        private static DateTime? ParseDate(string? raw) =>
            DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
    }
}
