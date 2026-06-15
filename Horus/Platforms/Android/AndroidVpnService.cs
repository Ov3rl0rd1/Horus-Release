using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    public class AndroidVpnService : IVpnPlatformService
    {
        public bool IsSupported => true;

        public TunnelState CurrentState => HorusVpnTunnelService.CurrentState;

        public event EventHandler<TunnelStateChangedEventArgs>? TunnelStateChanged
        {
            add => HorusVpnTunnelService.TunnelStateChanged += value;
            remove => HorusVpnTunnelService.TunnelStateChanged -= value;
        }

        public async Task<bool> RequestPermissionsAsync()
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity == null) return false;

            var intent = global::Android.Net.VpnService.Prepare(activity);
            if (intent == null) return true;

            var tcs = new TaskCompletionSource<bool>();
            VpnPermissionBroker.PendingCallback = result => tcs.TrySetResult(result);
            activity.StartActivityForResult(intent, VpnPermissionBroker.RequestCode);
            return await tcs.Task;
        }

        public Task StartTunnelAsync(TunnelOptions options, CancellationToken ct = default) =>
            HorusVpnTunnelService.StartTunnelAsync(options);

        public Task StopTunnelAsync() =>
            HorusVpnTunnelService.StopTunnelAsync();

        public Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules) =>
            Task.CompletedTask;

        public Task SetDnsAsync(string[] dnsServers) =>
            Task.CompletedTask;

        public long[] GetTunnelStats()
        {
            var stats = HevSocksTunnel.GetTunnelStats();

            if (stats == null)
                return new long[4] { 0, 0, 0, 0 };

            return stats;
        }
    }

    public static class VpnPermissionBroker
    {
        public const int RequestCode = 1001;
        public static Action<bool>? PendingCallback;
    }
}
