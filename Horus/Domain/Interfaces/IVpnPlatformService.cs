using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IVpnPlatformService
    {
        bool IsSupported { get; }
        TunnelState CurrentState { get; }

        Task<bool> RequestPermissionsAsync();
        Task StartTunnelAsync(TunnelOptions options, CancellationToken ct = default);
        Task StopTunnelAsync();
        Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules);
        Task SetDnsAsync(string[] dnsServers);
        long[] GetTunnelStats();

        event EventHandler<TunnelStateChangedEventArgs> TunnelStateChanged;
    }
}
