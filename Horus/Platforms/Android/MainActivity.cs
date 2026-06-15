using Android.App;
using Android.Content;
using Android.Content.PM;
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
    public class MainActivity : MauiAppCompatActivity
    {
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
