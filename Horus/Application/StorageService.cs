using Horus.Domain.Interfaces;

namespace Horus.Application
{
    public class StorageService : IStorageService
    {
        public Task Initialization { get; }

        private const string TOKEN_KEY = "api_token";
        private const string SESSION_KEY = "api_session";
        private const string USERNAME_KEY = "api_username";
        private const string HAS_SUBSCRIPTION_KEY = "api_has_subscription";
        private const string EXPIRE_DATE_KEY = "api_expire_date";

        private string? _session;
        private string? _token;
        private string? _username;
        private DateTime? _subscription;

        public StorageService()
        {
            Initialization = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            _token = await SecureStorage.GetAsync(TOKEN_KEY);
            _session = await SecureStorage.GetAsync(SESSION_KEY);
            _username = await SecureStorage.GetAsync(USERNAME_KEY);

            var hasSubStr = await SecureStorage.GetAsync(HAS_SUBSCRIPTION_KEY);
            if (bool.TryParse(hasSubStr, out bool hasSub) && hasSub)
            {
                var expStr = await SecureStorage.GetAsync(EXPIRE_DATE_KEY);
                if (DateTime.TryParse(expStr, out var exp))
                    _subscription = exp;
            }
        }

        public string? Session() => _session;
        public DateTime? Subscription() => _subscription;
        public string? Token() => _token;
        public string? Username() => _username;

        public async Task UpdateAsync(string token, string session, string username, DateTime? subscription)
        {
            _token = token;
            _session = session;
            _username = username;
            _subscription = subscription;

            await SecureStorage.SetAsync(TOKEN_KEY, token);
            await SecureStorage.SetAsync(SESSION_KEY, session);
            await SecureStorage.SetAsync(USERNAME_KEY, username);
            await SecureStorage.SetAsync(HAS_SUBSCRIPTION_KEY, subscription.HasValue.ToString());
            await SecureStorage.SetAsync(EXPIRE_DATE_KEY, subscription?.ToString("O") ?? string.Empty);
        }

        public async Task UpdateTokenAsync(string token)
        {
            _token = token;
            await SecureStorage.SetAsync(TOKEN_KEY, token);
        }

        public void Clear()
        {
            _token = null;
            _session = null;
            _username = null;
            _subscription = null;
            SecureStorage.RemoveAll();
        }
    }
}
