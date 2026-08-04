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

            await SecureStorage.SetAsync(SESSION_KEY, session);
            await WriteDateAsync(SESSION_EXPIRY_KEY, sessionExpiresAt);
        }

        public async Task UpdateAccountAsync(string username, string? email, DateTime? subscription)
        {
            _username = username;
            _email = email;
            _subscription = subscription;

            await SecureStorage.SetAsync(USERNAME_KEY, username);
            await SecureStorage.SetAsync(EMAIL_KEY, email ?? string.Empty);
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
            SecureStorage.RemoveAll();
        }

        private static Task WriteDateAsync(string key, DateTime? value) =>
            SecureStorage.SetAsync(key, value?.ToUniversalTime().ToString("O") ?? string.Empty);

        private static DateTime? ParseDate(string? raw) =>
            DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
    }
}
