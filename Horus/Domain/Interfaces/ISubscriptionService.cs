using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface ISubscriptionService
    {
        SubscriptionInfo? CurrentSubscription { get; }

        Task<SubscriptionInfo> CheckSubscriptionAsync();
        Task<IReadOnlyList<ServerInfo>> GetAvailableServersAsync();

        event EventHandler<SubscriptionChangedEventArgs> SubscriptionChanged;
    }
}
