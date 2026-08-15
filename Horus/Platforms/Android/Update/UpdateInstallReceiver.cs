using Android.App;
using Android.Content;
using Android.Content.PM;

namespace Horus.Platforms.Android.Update
{
    /// <summary>
    /// Receives the outcome of a <see cref="PackageInstaller"/> session.
    ///
    /// The success path is silent by design — the user is told about the new version once,
    /// on the next launch, and never mid-install. The case worth handling is
    /// <c>STATUS_PENDING_USER_ACTION</c>: the platform reserves the right to demand
    /// confirmation even when the session asked not to, and the documentation is explicit
    /// that the target-SDK bar for silent installs rises with each Android release. If that
    /// happens the confirmation intent has to be launched or the session simply hangs
    /// unresolved, and the update would look like it silently did nothing.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class UpdateInstallReceiver : BroadcastReceiver
    {
        public const string ActionInstallStatus = "com.horus.vpn.INSTALL_STATUS";

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context is null || intent is null) return;

            var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);
            var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);

            switch (status)
            {
                case (int)PackageInstallStatus.PendingUserAction:
                    LaunchConfirmation(context, intent);
                    break;

                case (int)PackageInstallStatus.Success:
                    // Nothing to do: the process is about to be replaced, and the "updated"
                    // notification is raised on the next launch from the recorded version.
                    break;

                default:
                    global::Android.Util.Log.Warn("Horus", $"Update install failed ({status}): {message}");
                    break;
            }
        }

        private static void LaunchConfirmation(Context context, Intent intent)
        {
            try
            {
                var confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent
                    : intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;

                if (confirm is null) return;
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("Horus", $"Could not show the install prompt: {ex.Message}");
            }
        }
    }
}
