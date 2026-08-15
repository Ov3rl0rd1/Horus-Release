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
        Name = PackageId + ".HorusVpnTunnelService",
        Permission = "android.permission.BIND_VPN_SERVICE",
        ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
    public class HorusVpnTunnelService : VpnService
    {
        /// <summary>
        /// Must match <c>$(ApplicationId)</c> in Horus.csproj and the class name
        /// libhev_socks.so binds its JNI entry points to. [Service(Name=…)] needs a
        /// compile-time constant, so it cannot read the MSBuild property directly.
        /// </summary>
        internal const string PackageId = "com.horus.vpn";

        private const string ChannelId = "horus_vpn_channel";
        private const int NotificationId = 42;
        private const string ActionStop = PackageId + ".STOP";

        private static TaskCompletionSource<bool>? _startTcs;
        private static TaskCompletionSource<bool>? _stopTcs;
        private static TunnelOptions? _pendingOptions;

        private ParcelFileDescriptor? _tunFd;

        /// <summary>
        /// The live instance, so the network monitor can push the underlying network onto
        /// it. Weakly held is unnecessary — the service outlives everything that uses this,
        /// and it is cleared in <see cref="OnDestroy"/>.
        /// </summary>
        private static HorusVpnTunnelService? _instance;

        /// <summary>Remembered so a service restart can re-apply it without a new callback.</summary>
        private static Network? _underlying;

        public static TunnelState CurrentState { get; private set; } = TunnelState.Unknown;
        public static event EventHandler<TunnelStateChangedEventArgs>? TunnelStateChanged;

        // Split tunneling state (set by AndroidSplitTunnelingService)
        public static SplitTunnelingMode SplitTunnelingMode { get; set; } = SplitTunnelingMode.Disabled;
        public static string[] SelectedApps { get; set; } = [];

        /// <summary>
        /// Packages that bypass the tunnel in every mode, from app configuration. Not part
        /// of the user's selection and not overridable by it.
        /// </summary>
        public static string[] AlwaysDirectApps { get; set; } = [];

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

        /// <summary>
        /// Tells the system which physical network is carrying the tunnel. Called on every
        /// handover by <see cref="AndroidNetworkMonitor"/>.
        ///
        /// Passing null hands the decision back to the system rather than asserting "no
        /// network", which is the correct thing to do while offline: asserting an empty
        /// array marks the VPN as having no connectivity, and some system components take
        /// that as licence to tear it down.
        /// </summary>
        internal static void SetUnderlyingNetwork(Network? network)
        {
            _underlying = network;
            _instance?.ApplyUnderlyingNetwork();
        }

        private void ApplyUnderlyingNetwork()
        {
            if (_tunFd is null) return; // nothing established yet; CreateTunnel will apply it
            SetUnderlyingNetworks(_underlying is null ? null : [_underlying]);
        }

        public override void OnCreate()
        {
            base.OnCreate();
            _instance = this;
        }

        public override void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
            base.OnDestroy();
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
            StartForeground(NotificationId, BuildNotification("Подключение…"));

            // A restarted service arrives with a null Intent and stale static state, so
            // there is nothing useful to resume — Sticky would leave a permanent
            // "Подключение…" notification attached to no tunnel.
            if (_pendingOptions == null)
            {
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            Task.Run(CreateTunnel);
            return StartCommandResult.NotSticky;
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
                var self = PackageName!;

                var builder = new Builder(this);
                builder.AddAddress(options.TunAddress, options.TunPrefix);
                builder.SetMtu(options.Mtu > 0 ? options.Mtu : 1500);
                builder.SetSession("Horus VPN");

                foreach (var dns in options.DnsServers ?? ["1.1.1.1", "8.8.8.8"])
                    builder.AddDnsServer(dns);

                if (options.AllTraffic)
                {
                    builder.AddRoute("0.0.0.0", 0);

                    // Capture IPv6 too. Without this every IPv6 connection on a dual-stack
                    // network bypasses the tunnel with the device's real address — a leak
                    // that is invisible until you look for it. The ULA matches the
                    // `ipv6: fc00::1` hev-socks5-tunnel is already configured with.
                    builder.AddAddress("fc00::1", 128);
                    builder.AddRoute("::", 0);
                }

                ApplySplitTunneling(builder, options, self);

                _tunFd = builder.Establish()
                    ?? throw new InvalidOperationException("VpnService.Builder.Establish() returned null.");

                // Immediately, not only on the next handover: whatever the monitor last saw
                // is the link this tunnel is being built on, and leaving it unset until
                // something changes means the first session of every connect is misattributed.
                ApplyUnderlyingNetwork();

                HevSocksTunnel.StartTunnel(_tunFd.Fd, options.SocksPort);

                UpdateNotification("Подключено");
                SetState(TunnelState.Started);
                _startTcs?.SetResult(true);
            }
            catch (Exception ex)
            {
                SetState(TunnelState.Error);
                _startTcs?.SetException(ex);
            }
        }

        /// <summary>
        /// Applies split tunneling, and — far more importantly — keeps this app's own UID
        /// out of the tunnel.
        ///
        /// xray-core is linked into this process, so its outbound socket to the VPN node
        /// runs under our UID. If that UID is routed into the TUN, the packet goes
        /// TUN → hev-socks5-tunnel → 127.0.0.1:1080 → xray → TUN and the tunnel deadlocks
        /// on itself. The upstream fix is a socket-protect callback, which this build of
        /// the core does not expose; excluding the UID achieves the same thing from the
        /// Android side.
        ///
        /// Consequence, and it is deliberate: the app's own API traffic bypasses the VPN.
        /// That keeps the API reachable when the tunnel is down — but it also means
        /// <c>GET /whoami</c> reports the device's real IP while connected, so it can
        /// never be used to verify the tunnel.
        /// </summary>
        private static void ApplySplitTunneling(Builder builder, TunnelOptions options, string self)
        {
            // Config-forced bypass applies in every mode. In Whitelist that means leaving
            // these packages out of the allowed set; in the other modes it means adding
            // them to the disallowed set.
            var forced = AlwaysDirectApps;

            // Whitelist mode excludes us by omission. Android throws
            // UnsupportedOperationException if allowed and disallowed apps are mixed on
            // one Builder, so this branch must never call AddDisallowedApplication.
            if (SplitTunnelingMode == SplitTunnelingMode.Whitelist)
            {
                var allowed = SelectedApps
                    .Where(p => p != self && !forced.Contains(p))
                    .ToArray();

                if (allowed.Length == 0)
                    throw new InvalidOperationException(
                        "Выберите хотя бы одно приложение для режима «только выбранные».");

                foreach (var pkg in allowed)
                {
                    try { builder.AddAllowedApplication(pkg); }
                    catch (global::Android.Content.PM.PackageManager.NameNotFoundException) { }
                }
                return;
            }

            // Blacklist and Disabled: our own UID first and unconditionally.
            builder.AddDisallowedApplication(self);

            var bypass = (SplitTunnelingMode == SplitTunnelingMode.Blacklist
                    ? SelectedApps
                    : options.BypassApps ?? [])
                .Concat(forced);

            foreach (var pkg in bypass.Distinct())
            {
                if (pkg == self) continue; // already excluded
                try { builder.AddDisallowedApplication(pkg); }
                catch (global::Android.Content.PM.PackageManager.NameNotFoundException) { }
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
            // minSdk is 26, so the channel API is always available here.
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
                .SetSmallIcon(Resource.Drawable.appicon_notif)
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
