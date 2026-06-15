using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    public class StubVpnPlatformService : IVpnPlatformService
    {
        public bool IsSupported => false;
        public TunnelState CurrentState => TunnelState.Unknown;
        public event EventHandler<TunnelStateChangedEventArgs>? TunnelStateChanged;

        public Task<bool> RequestPermissionsAsync() => Task.FromResult(false);
        public Task StartTunnelAsync(TunnelOptions options, CancellationToken ct = default) =>
            Task.FromException(new PlatformNotSupportedException("VPN tunnel not supported on this platform."));
        public Task StopTunnelAsync() => Task.CompletedTask;
        public Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules) => Task.CompletedTask;
        public Task SetDnsAsync(string[] dnsServers) => Task.CompletedTask;
        public long[] GetTunnelStats() => new long[4] { 0, 0, 0, 0 };
    }
}
