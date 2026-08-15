using Android.Content;
using Android.Net;
using Android.OS;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android.Update
{
    /// <summary>
    /// Reads the four device facts the deferral policy needs. Every one of them is a cheap
    /// synchronous system query, which is why the updater can afford to sample them every
    /// 15 minutes while an update waits for a good moment.
    /// </summary>
    public sealed class AndroidDeviceConditions : IDeviceConditions
    {
        public DeviceConditions Read()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                return new DeviceConditions(
                    HasNetwork: HasNetwork(context, out var unmetered),
                    IsUnmetered: unmetered,
                    IsCharging: IsCharging(context),
                    IsInteractive: IsInteractive(context));
            }
            catch
            {
                return DeviceConditions.Unknown;
            }
        }

        /// <summary>
        /// Reports the *underlying* network, not the tunnel.
        ///
        /// This matters: while Horus is connected the default network is the VPN, whose
        /// capabilities do not carry NOT_METERED. Asking about the default network would
        /// therefore report every connection as metered and a deferred update would wait
        /// forever on any device that keeps the VPN on — the exact usage this product is
        /// built for. So the VPN transport is skipped and the real link is inspected.
        /// </summary>
        private static bool HasNetwork(Context context, out bool unmetered)
        {
            unmetered = false;

            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm)
                return false;

            var networks = cm.GetAllNetworks();
            if (networks is null) return false;

            var any = false;
            foreach (var network in networks)
            {
                var caps = cm.GetNetworkCapabilities(network);
                if (caps is null) continue;
                if (!caps.HasCapability(NetCapability.Internet)) continue;
                if (!caps.HasCapability(NetCapability.Validated)) continue;
                if (caps.HasTransport(TransportType.Vpn)) continue;

                any = true;
                if (caps.HasCapability(NetCapability.NotMetered)) unmetered = true;
            }

            return any;
        }

        private static bool IsCharging(Context context)
        {
            using var filter = new IntentFilter(Intent.ActionBatteryChanged);
            using var status = context.RegisterReceiver(null, filter);
            if (status is null) return false;

            var plugged = status.GetIntExtra(BatteryManager.ExtraPlugged, 0);
            return plugged != 0;
        }

        private static bool IsInteractive(Context context) =>
            context.GetSystemService(Context.PowerService) is PowerManager pm && pm.IsInteractive;
    }
}
