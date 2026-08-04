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

            // Android 13+ suppresses the foreground-service notification unless this has
            // been granted — leaving the user with no visible sign the VPN is running and
            // no way to stop it from the shade. Declined is not fatal, so don't gate on it.
            await RequestNotificationPermissionAsync();

            var intent = global::Android.Net.VpnService.Prepare(activity);
            if (intent == null) return true;

            var tcs = new TaskCompletionSource<bool>();
            VpnPermissionBroker.PendingCallback = result => tcs.TrySetResult(result);
            activity.StartActivityForResult(intent, VpnPermissionBroker.RequestCode);
            return await tcs.Task;
        }

        private static async Task RequestNotificationPermissionAsync()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;

            try
            {
                var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                if (status != PermissionStatus.Granted)
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
            }
            catch { /* never block a connect on the notification prompt */ }
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
