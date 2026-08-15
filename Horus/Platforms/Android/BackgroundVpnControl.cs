using Horus.Application;
using Horus.Domain.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Turns the tunnel on or off from somewhere that has no UI — the Quick Settings tile
    /// and the after-update reconnect.
    ///
    /// Both entry points run in the app process (Android starts it to deliver a tile click
    /// or a package-replaced broadcast), so the DI graph <see cref="MauiProgram"/> built is
    /// available and the ordinary connect path can be reused. What is <i>not</i> available
    /// is an Activity, which is why <see cref="AndroidVpnService.RequestPermissionsAsync"/>
    /// had to stop demanding one before checking whether consent already exists.
    ///
    /// Starting a foreground service from the background is restricted on Android 12+. A
    /// VPN service is not automatically exempt, but an app the user has excluded from
    /// battery optimisation is — which is the same exemption the tunnel needs to survive
    /// Doze, so the two requirements point at the same request. When it is refused the
    /// caller gets false rather than a crash, and says so in the only way it can.
    /// </summary>
    internal static class BackgroundVpnControl
    {
        public static VpnManager? Resolve() =>
            IPlatformApplication.Current?.Services?.GetService<VpnManager>();

        public static bool IsConnected => Resolve()?.State == VpnState.Connected;

        public static async Task<bool> TryConnectAsync()
        {
            try
            {
                var manager = Resolve();
                if (manager is null) return false;
                if (manager.State is VpnState.Connected or VpnState.Connecting) return true;

                // No consent and no Activity to ask with: the caller has to send the user
                // into the app instead of failing silently.
                if (!AndroidVpnService.HasConsent()) return false;

                await manager.ConnectAsync().ConfigureAwait(false);
                return manager.State == VpnState.Connected;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"Background connect failed: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> TryDisconnectAsync()
        {
            try
            {
                var manager = Resolve();
                if (manager is null) return false;
                await manager.DisconnectAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"Background disconnect failed: {ex.Message}");
                return false;
            }
        }
    }
}
