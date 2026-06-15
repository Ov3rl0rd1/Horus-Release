using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class SubscriptionService : ISubscriptionService
    {
        private readonly IApiService _api;
        private readonly IStorageService _storage;

        private SubscriptionInfo? _current;

        public SubscriptionService(IApiService api, IStorageService storage)
        {
            _api = api;
            _storage = storage;
        }

        public SubscriptionInfo? CurrentSubscription => _current;

        public event EventHandler<SubscriptionChangedEventArgs>? SubscriptionChanged;

        public Task<SubscriptionInfo> CheckSubscriptionAsync()
        {
            var expiry = _storage.Subscription();
            _current = new SubscriptionInfo
            {
                APIKey = _storage.Token() ?? string.Empty,
                ExpireAt = expiry ?? DateTime.MinValue
            };

            bool isExpired = _current.ExpireAt <= DateTime.UtcNow;
            SubscriptionChanged?.Invoke(this, new SubscriptionChangedEventArgs(_current, isExpired));
            return Task.FromResult(_current);
        }

        public async Task<IReadOnlyList<ServerInfo>> GetAvailableServersAsync()
        {
            return await _api.GetServersAsync();
        }
    }
}
