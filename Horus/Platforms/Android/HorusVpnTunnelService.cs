using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Horus.Domain.Events;
using Horus.Domain.Models;
using SplitTunnelingMode = Horus.Domain.Models.SplitTunnelingMode;

namespace Horus.Platforms.Android
{
    [Service(
        Name = "com.horus_beta.vpn.HorusVpnTunnelService",
        Permission = "android.permission.BIND_VPN_SERVICE",
        ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
    public class HorusVpnTunnelService : VpnService
    {
        private const string ChannelId = "horus_vpn_channel";
        private const int NotificationId = 42;
        private const string ActionStop = "com.horus_beta.vpn.STOP";

        private static TaskCompletionSource<bool>? _startTcs;
        private static TaskCompletionSource<bool>? _stopTcs;
        private static TunnelOptions? _pendingOptions;

        private ParcelFileDescriptor? _tunFd;

        public static TunnelState CurrentState { get; private set; } = TunnelState.Unknown;
        public static event EventHandler<TunnelStateChangedEventArgs>? TunnelStateChanged;

        // Split tunneling state (set by AndroidSplitTunnelingService)
        public static SplitTunnelingMode SplitTunnelingMode { get; set; } = SplitTunnelingMode.Disabled;
        public static string[] SelectedApps { get; set; } = [];

        internal static Task StartTunnelAsync(TunnelOptions options)
        {
            _pendingOptions = options;
            _startTcs = new TaskCompletionSource<bool>();
            var intent = new Intent(global::Android.App.Application.Context, typeof(HorusVpnTunnelService));
            global::Android.App.Application.Context.StartForegroundService(intent);
            return _startTcs.Task;
        }

        internal static Task StopTunnelAsync()
        {
            _stopTcs = new TaskCompletionSource<bool>();
            var intent = new Intent(global::Android.App.Application.Context, typeof(HorusVpnTunnelService));
            intent.SetAction(ActionStop);
            global::Android.App.Application.Context.StartService(intent);
            return _stopTcs.Task;
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == ActionStop)
            {
                CloseTunnel();
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            EnsureNotificationChannel();
            StartForeground(NotificationId, BuildNotification("Connecting..."));
            Task.Run(CreateTunnel);
            return StartCommandResult.Sticky;
        }

        public override void OnRevoke()
        {
            CloseTunnel();
            base.OnRevoke();
        }

        private void CreateTunnel()
        {
            var options = _pendingOptions;
            if (options == null)
            {
                _startTcs?.SetException(new InvalidOperationException("No tunnel options provided."));
                return;
            }

            SetState(TunnelState.Starting);
            try
            {
                var builder = new Builder(this);
                builder.AddAddress(options.TunAddress, options.TunPrefix);
                builder.SetMtu(options.Mtu > 0 ? options.Mtu : 1500);
                builder.SetSession("Horus VPN");

                foreach (var dns in options.DnsServers ?? ["1.1.1.1", "8.8.8.8"])
                    builder.AddDnsServer(dns);

                if (options.AllTraffic)
                    builder.AddRoute("0.0.0.0", 0);

                // Apply split tunneling
                switch (SplitTunnelingMode)
                {
                    case SplitTunnelingMode.Blacklist:
                        // Listed apps bypass VPN; all others go through it
                        foreach (var pkg in SelectedApps)
                        {
                            try { builder.AddDisallowedApplication(pkg); }
                            catch { /* ignore unknown packages */ }
                        }
                        break;

                    case SplitTunnelingMode.Whitelist:
                        // Only listed apps go through VPN; all others bypass
                        foreach (var pkg in SelectedApps)
                        {
                            try { builder.AddAllowedApplication(pkg); }
                            catch { /* ignore unknown packages */ }
                        }
                        break;

                    default:
                        // options.BypassApps from connection config (legacy)
                        foreach (var pkg in options.BypassApps ?? [])
                        {
                            try { builder.AddDisallowedApplication(pkg); }
                            catch { }
                        }
                        break;
                }

                _tunFd = builder.Establish()
                    ?? throw new InvalidOperationException("VpnService.Builder.Establish() returned null.");

                HevSocksTunnel.StartTunnel(_tunFd.Fd);

                UpdateNotification("Connected");
                SetState(TunnelState.Started);
                _startTcs?.SetResult(true);
            }
            catch (Exception ex)
            {
                SetState(TunnelState.Error);
                _startTcs?.SetException(ex);
            }
        }

        private void CloseTunnel()
        {
            SetState(TunnelState.Stopping);

            HevSocksTunnel.StopTunnel();

            try
            {
                _tunFd?.Close();
                _tunFd = null;
            }
            catch { }

            StopForeground(StopForegroundFlags.Remove);
            SetState(TunnelState.Stopped);
            _stopTcs?.SetResult(true);
        }

        private void EnsureNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            var nm = (NotificationManager?)GetSystemService(NotificationService);
            if (nm?.GetNotificationChannel(ChannelId) == null)
            {
                var channel = new NotificationChannel(ChannelId, "VPN", NotificationImportance.Low)
                {
                    Description = "Horus VPN tunnel status"
                };
                nm?.CreateNotificationChannel(channel);
            }
        }

        private Notification BuildNotification(string text)
        {
            var builder = new Notification.Builder(this, ChannelId)
                .SetContentTitle("Horus VPN")
                .SetContentText(text)
                .SetSmallIcon(Resource.Mipmap.appicon)
                .SetOngoing(true);
            return builder.Build();
        }

        private void UpdateNotification(string text)
        {
            var nm = (NotificationManager?)GetSystemService(NotificationService);
            nm?.Notify(NotificationId, BuildNotification(text));
        }

        private static void SetState(TunnelState state)
        {
            CurrentState = state;
            TunnelStateChanged?.Invoke(null, new TunnelStateChangedEventArgs(state, null));
        }
    }
}
