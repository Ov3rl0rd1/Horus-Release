using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class SubscriptionService : ISubscriptionService
    {
        public SubscriptionInfo? CurrentSubscription => throw new NotImplementedException();

        public event EventHandler<SubscriptionChangedEventArgs> SubscriptionChanged;

        public Task<SubscriptionInfo> CheckSubscriptionAsync()
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<ServerInfo>> GetAvailableServersAsync()
        {
            throw new NotImplementedException();
        }
    }
}
