using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Horus.Application;
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
        /// The live instance, so the network monitor can push the underlying networks onto
        /// it. Weakly held is unnecessary — the service outlives everything that uses this,
        /// and it is cleared in <see cref="OnDestroy"/>.
        /// </summary>
        private static HorusVpnTunnelService? _instance;

        /// <summary>Remembered so a service restart can re-apply it without a new callback.</summary>
        private static Network[]? _underlying;

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
            _startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var intent = new Intent(global::Android.App.Application.Context, typeof(HorusVpnTunnelService));
            global::Android.App.Application.Context.StartForegroundService(intent);
            return _startTcs.Task;
        }

        internal static Task StopTunnelAsync()
        {
            _stopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var intent = new Intent(global::Android.App.Application.Context, typeof(HorusVpnTunnelService));
            intent.SetAction(ActionStop);
            global::Android.App.Application.Context.StartService(intent);
            return _stopTcs.Task;
        }

        /// <summary>
        /// Tells the system which physical networks are carrying the tunnel, in priority
        /// order — index 0 is preferred. Called on every handover by
        /// <see cref="AndroidNetworkMonitor"/>.
        ///
        /// <para>Null and empty are not the same thing and the difference matters. Null
        /// hands the decision back to the system, which is correct while offline: asserting
        /// an empty array marks the VPN as having no connectivity, and some system
        /// components take that as licence to tear it down. Empty is therefore never passed
        /// on — it is normalised to null in <see cref="ApplyUnderlyingNetwork"/>.</para>
        /// </summary>
        internal static void SetUnderlyingNetwork(Network[]? networks)
        {
            _underlying = networks;
            _instance?.ApplyUnderlyingNetwork();
        }

        private void ApplyUnderlyingNetwork()
        {
            if (_tunFd is null) return; // nothing established yet; CreateTunnel will apply it

            try
            {
                SetUnderlyingNetworks(_underlying is { Length: > 0 } ? _underlying : null);
            }
            catch (Exception ex)
            {
                Diag.Warn("tun", $"setUnderlyingNetworks failed: {ex.Message}");
            }
        }

        public override void OnCreate()
        {
            base.OnCreate();
            _instance = this;
        }

        public override void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
            Diag.Info("tun", "service destroyed");
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

            // Foreground first, and within five seconds of being started, or the system
            // kills the service outright. Everything expensive happens after this returns.
            if (!TryStartForeground())
            {
                _startTcs?.TrySetException(new InvalidOperationException(
                    "Система не разрешила запустить фоновую службу VPN."));
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            // A null intent means the system restarted us after the process died. Static
            // state went with it, so there is nothing to resume — but if the user never
            // turned the VPN off, the right move is to run the whole connect path again
            // rather than sit here with a notification attached to no tunnel.
            if (_pendingOptions is null)
            {
                if (intent is null && VpnIntent.Active) return ResumeAfterProcessDeath();

                Diag.Info("tun", $"start with no options (intent? {intent is not null}); stopping");
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            Task.Run(CreateTunnel);

            // Sticky: if the process is reclaimed while the tunnel is up, Android brings the
            // service back and the branch above rebuilds the connection. This is the single
            // change that turns "the VPN was off in the morning" into "it came back".
            return StartCommandResult.Sticky;
        }

        /// <summary>
        /// Rebuilds a connection the system asked us to resume.
        ///
        /// <para>Budgeted on purpose. A connect that fails immediately would otherwise
        /// become a restart loop — service starts, fails, stops, Android restarts it — that
        /// costs far more battery than the leak any of this was meant to fix.
        /// <see cref="VpnIntent.TryConsumeRestart"/> allows a few attempts inside a rolling
        /// window and then stands down, leaving the user a notification instead. Same
        /// reasoning as NekoBox refusing to restart a child process that exits within a
        /// second of starting.</para>
        /// </summary>
        private StartCommandResult ResumeAfterProcessDeath()
        {
            if (!VpnIntent.TryConsumeRestart())
            {
                UpdateNotification("Не удалось восстановить подключение");
                StopForeground(StopForegroundFlags.Detach);
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            Diag.Warn("tun", "restarted by the system; re-running connect");
            UpdateNotification("Восстановление подключения…");

            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await BackgroundVpnControl.TryConnectAsync().ConfigureAwait(false);
                    if (!ok)
                    {
                        Diag.Error("tun", "post-restart reconnect failed");
                        UpdateNotification("Не удалось восстановить подключение");
                        StopSelf();
                    }
                }
                catch (Exception ex)
                {
                    Diag.Error("tun", $"post-restart reconnect threw: {ex.Message}");
                    StopSelf();
                }
            });

            return StartCommandResult.Sticky;
        }

        /// <summary>
        /// Enters the foreground, reporting failure rather than throwing.
        ///
        /// <para>Android 12+ can refuse a background start outright
        /// (<c>ForegroundServiceStartNotAllowedException</c>), and 14+ can reject the
        /// declared service type. Both used to take the process down from a background
        /// thread with nothing written anywhere; a false return lets the caller stop
        /// cleanly and say why.</para>
        /// </summary>
        private bool TryStartForeground()
        {
            try
            {
                EnsureNotificationChannel();
                StartForeground(NotificationId, BuildNotification("Подключение…"));
                return true;
            }
            catch (Exception ex)
            {
                Diag.Error("tun", $"startForeground refused: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public override void OnRevoke()
        {
            // The user handed the VPN slot to another app, or turned Horus off in system
            // settings. Clearing the intent is what stops every automatic path from
            // fighting that decision for the rest of the device's life.
            Diag.User("tun", "VPN revoked by the system");
            VpnIntent.Clear();

            CloseTunnel();
            base.OnRevoke();
        }

        private void CreateTunnel()
        {
            var options = _pendingOptions;
            if (options == null)
            {
                _startTcs?.TrySetException(new InvalidOperationException("No tunnel options provided."));
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
                Diag.Info("tun", $"established, mtu {options.Mtu}, socks {options.SocksPort}");
                _startTcs?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Diag.Error("tun", $"establish failed: {ex.Message}");
                SetState(TunnelState.Error);
                _startTcs?.TrySetException(ex);
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

            // A false here means the bridge thread did not unwind, so a later start would
            // put a second reader on the same TUN fd. HevSocksTunnel.StartTunnel refuses in
            // that case; logging it is what makes the resulting failed connect explicable.
            if (!HevSocksTunnel.StopTunnel())
                Diag.Error("tun", "bridge did not stop cleanly; next connect will be refused until it does");

            try
            {
                _tunFd?.Close();
                _tunFd = null;
            }
            catch { }

            // Cleared so a later start cannot pick up options belonging to a connection
            // that is already gone.
            _pendingOptions = null;

            StopForeground(StopForegroundFlags.Remove);
            SetState(TunnelState.Stopped);
            _stopTcs?.TrySetResult(true);
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

        /// <summary>
        /// Repaints the ongoing notification. Only ever called on a state change — never on
        /// a timer. Rebuilding a Notification and handing it to SystemUI once a second is
        /// one of the larger avoidable battery costs in a VPN client, and there is nothing
        /// here worth paying it for.
        /// </summary>
        private void UpdateNotification(string text)
        {
            try
            {
                var nm = (NotificationManager?)GetSystemService(NotificationService);
                nm?.Notify(NotificationId, BuildNotification(text));
            }
            catch (Exception ex) { Diag.Warn("tun", $"notification update failed: {ex.Message}"); }
        }

        private static void SetState(TunnelState state)
        {
            CurrentState = state;
            TunnelStateChanged?.Invoke(null, new TunnelStateChangedEventArgs(state, null));
        }
    }
}
