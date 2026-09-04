using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;
using Horus.Platforms.Android;

namespace Horus
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges =
            ConfigChanges.ScreenSize | ConfigChanges.Orientation |
            ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
            ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    // Long-pressing a Quick Settings tile opens whatever activity answers
    // ACTION_QS_TILE_PREFERENCES. Without this the gesture does nothing at all, which reads
    // as a broken tile — Android offers no default for it.
    [IntentFilter([ActionQsTilePreferences], Categories = [Intent.CategoryDefault])]
    public class MainActivity : MauiAppCompatActivity
    {
        private const string ActionQsTilePreferences =
            "android.service.quicksettings.action.QS_TILE_PREFERENCES";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Tint the system bars to match the app's dark theme (default was MAUI purple).
            if (Window is not null)
            {
                Window.SetStatusBarColor(global::Android.Graphics.Color.ParseColor("#0B0512"));
                Window.SetNavigationBarColor(global::Android.Graphics.Color.ParseColor("#0E0718"));

                // Dark bars → light (white) icons.
                var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
                if (controller is not null)
                {
                    controller.AppearanceLightStatusBars = false;
                    controller.AppearanceLightNavigationBars = false;
                }
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            if (requestCode == VpnPermissionBroker.RequestCode)
            {
                VpnPermissionBroker.PendingCallback?.Invoke(resultCode == Result.Ok);
                VpnPermissionBroker.PendingCallback = null;
            }
            base.OnActivityResult(requestCode, resultCode, data);
        }
    }
}
