using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface ISubscriptionService
    {
        SubscriptionInfo? CurrentSubscription { get; }

        /// <summary>Re-reads subscription state from <c>GET /whoami</c>, falling back to the cached expiry.</summary>
        Task<SubscriptionInfo> CheckSubscriptionAsync();

        /// <summary>
        /// Applies an already-fetched <c>/whoami</c> snapshot. <see cref="IAccountSync"/>
        /// uses this so one request serves the auth state, the stored expiry and this
        /// service, instead of each of them making its own call.
        /// </summary>
        /// <param name="me">The snapshot, or null when the call did not come back.</param>
        Task<SubscriptionInfo> ApplyAsync(WhoAmIResponse? me);

        /// <summary>Server catalogue from <c>GET /servers/best</c> (least-loaded first, capacity available).</summary>
        /// <summary>
        /// Candidate nodes, newest capacity figures from the API.
        ///
        /// <para>Set <paramref name="measureLatency"/> to have each one TCP-probed and
        /// <see cref="ServerInfo.PingMs"/> filled in, ordered fastest-first. Off by
        /// default: only the server picker needs the measurement, and it costs a
        /// connection attempt per node.</para>
        /// </summary>
        Task<IReadOnlyList<ServerInfo>> GetAvailableServersAsync(
            bool measureLatency = false, CancellationToken ct = default);

        event EventHandler<SubscriptionChangedEventArgs> SubscriptionChanged;
    }
}
