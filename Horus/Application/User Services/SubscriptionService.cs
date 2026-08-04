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

        public async Task<SubscriptionInfo> CheckSubscriptionAsync()
        {
            // /whoami is authoritative; the persisted expiry keeps the UI honest offline.
            var me = await _api.GetWhoAmIAsync();
            if (me is not null)
                await _storage.UpdateSubscriptionAsync(me.SubscriptionExpiresAt);

            var expiry = me?.SubscriptionExpiresAt ?? _storage.Subscription();

            _current = new SubscriptionInfo
            {
                APIKey = _storage.Session() ?? string.Empty,
                ExpireAt = expiry ?? DateTime.MinValue
            };

            SubscriptionChanged?.Invoke(this,
                new SubscriptionChangedEventArgs(_current, !_current.IsActive));

            return _current;
        }

        public async Task<IReadOnlyList<ServerInfo>> GetAvailableServersAsync()
        {
            try
            {
                var list = await _api.GetServersAsync();
                if (list is { Count: > 0 }) return list;
            }
            catch { /* fall through to the fallback below */ }

#if DEBUG
            // DEBUG: no API? show a mock catalogue so the UI is browsable offline.
            return MockServers();
#else
            return Array.Empty<ServerInfo>();
#endif
        }

#if DEBUG
        private static IReadOnlyList<ServerInfo> MockServers() => new List<ServerInfo>
        {
            new() { Id = 1, Country = "FI", Name = "Финляндия", City = "Хельсинки", Host = "fi1.horus.dev", CurrentLoad = 32, MaxClients = 100, PingMs = 19 },
            new() { Id = 2, Country = "NL", Name = "Нидерланды", City = "Амстердам",  Host = "nl1.horus.dev", CurrentLoad = 41, MaxClients = 100, PingMs = 24 },
            new() { Id = 3, Country = "DE", Name = "Германия",   City = "Франкфурт",  Host = "de1.horus.dev", CurrentLoad = 56, MaxClients = 100, PingMs = 31 },
            new() { Id = 4, Country = "KZ", Name = "Казахстан",  City = "Алматы",     Host = "kz1.horus.dev", CurrentLoad = 48, MaxClients = 100, PingMs = 37 },
            new() { Id = 5, Country = "TR", Name = "Турция",     City = "Стамбул",    Host = "tr1.horus.dev", CurrentLoad = 62, MaxClients = 100, PingMs = 48 },
            new() { Id = 6, Country = "US", Name = "США",        City = "Нью-Йорк",   Host = "us1.horus.dev", CurrentLoad = 27, MaxClients = 100, PingMs = 118 },
        };
#endif
    }
}
