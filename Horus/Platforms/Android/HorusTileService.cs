using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Service.QuickSettings;
using Horus.Domain.Events;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// The Quick Settings tile: turn the tunnel on or off from the notification shade
    /// without opening the app.
    ///
    /// The tile is only listening while the shade is open, so it subscribes to
    /// <c>VpnManager.StateChanged</c> in <see cref="OnStartListening"/> and drops the
    /// subscription in <see cref="OnStopListening"/> — a tile that stayed subscribed would
    /// hold a reference to the manager for the life of the process and repaint nothing
    /// anyone can see.
    ///
    /// Two states are shown rather than three: Android's tile has no "busy", so connecting
    /// is painted as active with a label that says so, which is closer to the truth than
    /// showing it as off while a tunnel comes up.
    /// </summary>
    [Service(
        Name = HorusVpnTunnelService.PackageId + ".HorusTileService",
        Permission = "android.permission.BIND_QUICK_SETTINGS_TILE",
        Icon = "@drawable/appicon_notif",
        Label = "Horus VPN",
        Exported = true)]
    [IntentFilter([ActionQsTile])]
    public class HorusTileService : TileService
    {
        private const string ActionQsTile = "android.service.quicksettings.action.QS_TILE";

        private EventHandler<VpnStateChangedEventArgs>? _handler;

        public override void OnStartListening()
        {
            base.OnStartListening();

            var manager = BackgroundVpnControl.Resolve();
            if (manager is not null)
            {
                _handler = (_, __) => Paint();
                manager.StateChanged += _handler;
            }

            Paint();
        }

        public override void OnStopListening()
        {
            var manager = BackgroundVpnControl.Resolve();
            if (manager is not null && _handler is not null) manager.StateChanged -= _handler;
            _handler = null;

            base.OnStopListening();
        }

        public override void OnClick()
        {
            base.OnClick();

            // No consent yet means a system dialog only an Activity can raise. Send the
            // user into the app rather than toggling a tile that cannot do anything.
            if (!AndroidVpnService.HasConsent())
            {
                OpenApp();
                return;
            }

            var manager = BackgroundVpnControl.Resolve();
            var connected = manager?.State is VpnState.Connected or VpnState.Connecting;

            // Repaint straight away: a tile that does not react for the several seconds a
            // connect takes reads as broken, and the state change will correct it.
            SetTile(connected ? VpnState.Disconnecting : VpnState.Connecting);

            _ = Task.Run(async () =>
            {
                if (connected) await BackgroundVpnControl.TryDisconnectAsync().ConfigureAwait(false);
                else if (!await BackgroundVpnControl.TryConnectAsync().ConfigureAwait(false)) OpenApp();
                Paint();
            });
        }

        private void OpenApp()
        {
            try
            {
                var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
                if (launch is null) return;
                launch.AddFlags(ActivityFlags.NewTask);

                if (OperatingSystem.IsAndroidVersionAtLeast(34))
                {
                    var pending = PendingIntent.GetActivity(this, 0, launch,
                        PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                    StartActivityAndCollapse(pending!);
                }
                else
                {
#pragma warning disable CA1422 // the Intent overload is the only one before API 34
                    StartActivityAndCollapse(launch);
#pragma warning restore CA1422
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"Tile could not open the app: {ex.Message}");
            }
        }

        private void Paint() => SetTile(BackgroundVpnControl.Resolve()?.State ?? VpnState.Disconnected);

        private void SetTile(VpnState state)
        {
            try
            {
                var tile = QsTile;
                if (tile is null) return;

                var on = state is VpnState.Connected or VpnState.Connecting;
                tile.State = on ? TileState.Active : TileState.Inactive;
                tile.Label = "Horus VPN";
                tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.appicon_notif);

                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    tile.Subtitle = state switch
                    {
                        VpnState.Connected => "Защищено",
                        VpnState.Connecting => "Подключение…",
                        VpnState.Disconnecting => "Отключение…",
                        _ => "Отключено"
                    };
                }

                tile.UpdateTile();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"Tile repaint failed: {ex.Message}");
            }
        }
    }
}
