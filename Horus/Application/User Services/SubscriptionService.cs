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

        /// <summary>
        /// Fetches <c>/whoami</c> and applies it. Prefer <see cref="ApplyAsync"/> when a
        /// snapshot is already in hand — this overload exists for callers that have none.
        /// </summary>
        public async Task<SubscriptionInfo> CheckSubscriptionAsync() =>
            await ApplyAsync(await _api.GetWhoAmIAsync());

        public async Task<SubscriptionInfo> ApplyAsync(WhoAmIResponse? me)
        {
            // /whoami is authoritative; the persisted expiry keeps the UI honest offline.
            if (me is not null)
                await _storage.UpdateSubscriptionAsync(me.SubscriptionExpiresAt);

            var expiry = me?.SubscriptionExpiresAt ?? _storage.Subscription();

            var previous = _current;
            _current = new SubscriptionInfo
            {
                APIKey = _storage.Session() ?? string.Empty,
                ExpireAt = expiry ?? DateTime.MinValue
            };

            // Only announce an actual change. The poll runs every 20 s while a grant is
            // awaited, and re-raising an unchanged expiry that often would have every
            // subscriber redrawing on a timer.
            if (previous is null || previous.ExpireAt != _current.ExpireAt)
                SubscriptionChanged?.Invoke(this,
                    new SubscriptionChangedEventArgs(_current, !_current.IsActive));

            return _current;
        }

        public async Task<IReadOnlyList<ServerInfo>> GetAvailableServersAsync(
            bool measureLatency = false, CancellationToken ct = default)
        {
            IReadOnlyList<ServerInfo>? list = null;

            try { list = await _api.GetServersAsync(ct); }
            catch { /* fall through to the fallback below */ }

            if (list is not { Count: > 0 })
            {
#if DEBUG
                // DEBUG: no API? show a mock catalogue so the UI is browsable offline.
                list = MockServers();
#else
                return Array.Empty<ServerInfo>();
#endif
            }

            // The API cannot measure this for us — it only knows its own distance to each
            // node, not the user's. That is the whole reason the endpoint returns a list of
            // candidates instead of a decision.
            if (measureLatency) list = await LatencyProbe.MeasureAsync(list, ct);

            return list;
        }

#if DEBUG
        private static IReadOnlyList<ServerInfo> MockServers() => new List<ServerInfo>
        {
            new() { Id = 1, Country = "FI", City = "Хельсинки", Host = "fi1.horus.dev", CurrentLoad = 32, ReservedCount = 40, MaxClients = 100, PingMs = 19 },
            new() { Id = 2, Country = "NL", City = "Амстердам",  Host = "nl1.horus.dev", CurrentLoad = 41, ReservedCount = 55, MaxClients = 100, PingMs = 24 },
            new() { Id = 3, Country = "DE", City = "Франкфурт",  Host = "de1.horus.dev", CurrentLoad = 56, ReservedCount = 70, MaxClients = 100, PingMs = 31 },
            new() { Id = 4, Country = "KZ", City = "Алматы",     Host = "kz1.horus.dev", CurrentLoad = 48, ReservedCount = 61, MaxClients = 100, PingMs = 37 },
            new() { Id = 5, Country = "TR", City = "Стамбул",    Host = "tr1.horus.dev", CurrentLoad = 62, ReservedCount = 80, MaxClients = 100, PingMs = 48 },
            new() { Id = 6, Country = "US", City = "Нью-Йорк",   Host = "us1.horus.dev", CurrentLoad = 27, ReservedCount = 33, MaxClients = 100, PingMs = 118 },
        };
#endif
    }
}
