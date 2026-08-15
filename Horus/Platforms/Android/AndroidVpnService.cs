using Android.Content;
using Android.OS;
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
            // Consent first, and against the application context — VpnService.Prepare
            // returns null once the user has granted it, and that grant survives app
            // updates and reboots. Asking for an Activity before checking meant every
            // connect attempt with no UI on screen failed outright, which is exactly the
            // case for the Quick Settings tile and for reconnecting after an update.
            var context = global::Android.App.Application.Context;
            var intent = global::Android.Net.VpnService.Prepare(context);

            if (intent == null)
            {
                await RequestNotificationPermissionAsync();
                RequestBatteryExemptionOnce();
                return true;
            }

            // Consent is genuinely needed, and only an Activity can ask for it.
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity == null) return false;

            // Android 13+ suppresses the foreground-service notification unless this has
            // been granted — leaving the user with no visible sign the VPN is running and
            // no way to stop it from the shade. Declined is not fatal, so don't gate on it.
            await RequestNotificationPermissionAsync();

            var tcs = new TaskCompletionSource<bool>();
            VpnPermissionBroker.PendingCallback = result => tcs.TrySetResult(result);
            activity.StartActivityForResult(intent, VpnPermissionBroker.RequestCode);
            var granted = await tcs.Task;

            if (granted) RequestBatteryExemptionOnce();
            return granted;
        }

        /// <summary>
        /// Asks, once ever, to be left out of battery optimisation.
        ///
        /// This is the single most load-bearing permission for a tunnel that has to survive
        /// the night. Doze suspends the app's network access in its idle windows, and the
        /// connection dies with no event to notice it by — which is the "отключается при
        /// входе в спящий режим" symptom exactly. The exemption also permits starting a
        /// foreground service from the background, which is what makes the Quick Settings
        /// tile and the reconnect-after-update path work.
        ///
        /// Asked once and never again: a VPN that nags on every connect is a VPN people
        /// uninstall, and the answer is remembered by the system either way.
        /// </summary>
        private static void RequestBatteryExemptionOnce()
        {
            const string askedKey = "horus.battery.exemption.asked";

            try
            {
                var context = global::Android.App.Application.Context;
                if (context.GetSystemService(Context.PowerService) is not PowerManager power) return;

                // Already exempt: nothing to ask, and clear the flag's relevance.
                if (power.IsIgnoringBatteryOptimizations(context.PackageName!)) return;
                if (Preferences.Get(askedKey, false)) return;

                Preferences.Set(askedKey, true);

                var intent = new Intent(
                    global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                    global::Android.Net.Uri.Parse("package:" + context.PackageName));
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch (Exception ex)
            {
                // Some OEM builds hide this screen outright. Not being able to ask is not a
                // reason to fail a connect.
                global::Android.Util.Log.Warn("Horus", $"Battery exemption request failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether consent is already in place, without asking for it. Used by callers that
        /// have no UI and must not try to open one.
        /// </summary>
        public static bool HasConsent() =>
            global::Android.Net.VpnService.Prepare(global::Android.App.Application.Context) is null;

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

        /// <summary>
        /// The bridge's cumulative counters, or an <b>empty</b> array when the bridge is
        /// not running.
        ///
        /// The distinction matters. This used to return four zeros in that case, which is
        /// indistinguishable from "the device is idle" — so a health check reading it
        /// concluded the tunnel was fine precisely when the component moving the packets
        /// had died. Callers must treat a short array as "unknown", never as "no traffic".
        /// </summary>
        public long[] GetTunnelStats() => HevSocksTunnel.GetTunnelStats() ?? [];
    }

    public static class VpnPermissionBroker
    {
        public const int RequestCode = 1001;
        public static Action<bool>? PendingCallback;
    }
}
