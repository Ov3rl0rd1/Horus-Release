using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface ISubscriptionService
    {
        SubscriptionInfo? CurrentSubscription { get; }

        /// <summary>Re-reads subscription state from <c>GET /whoami</c>, falling back to the cached expiry.</summary>
        Task<SubscriptionInfo> CheckSubscriptionAsync();

        /// <summary>Server catalogue from <c>GET /servers/best</c> (least-loaded first, capacity available).</summary>
        Task<IReadOnlyList<ServerInfo>> GetAvailableServersAsync();

        event EventHandler<SubscriptionChangedEventArgs> SubscriptionChanged;
    }
}
