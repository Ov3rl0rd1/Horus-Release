using Android.Content;
using Android.OS;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Reads the three Android switches that quietly disable features, and opens the screens
    /// that flip them.
    ///
    /// <para><b>Launching the settings screen needs the foreground Activity.</b> Android 10
    /// blocks activity starts from the background, so a bare
    /// <c>context.StartActivity(NEW_TASK)</c> from a service or a receiver fails silently —
    /// which is precisely how the previous install-permission prompt disappeared without
    /// trace. These are only ever called from a button, so the Activity is there; the
    /// NEW_TASK path is a fallback, and its failure is reported rather than swallowed.</para>
    /// </summary>
    public sealed class AndroidSystemPermissions : ISystemPermissions
    {
        private bool _canInstall;
        private bool _notifications;
        private bool _batteryExempt;
        private bool _read;

        public event EventHandler? Changed;

        public bool CanInstallPackages { get { EnsureRead(); return _canInstall; } }
        public bool NotificationsEnabled { get { EnsureRead(); return _notifications; } }
        public bool IgnoringBatteryOptimisations { get { EnsureRead(); return _batteryExempt; } }

        private void EnsureRead()
        {
            if (_read) return;
            _read = true;
            ReadAll(out _canInstall, out _notifications, out _batteryExempt);
        }

        public void Refresh()
        {
            ReadAll(out var install, out var notifications, out var battery);

            var moved = !_read
                || install != _canInstall
                || notifications != _notifications
                || battery != _batteryExempt;

            _canInstall = install;
            _notifications = notifications;
            _batteryExempt = battery;
            _read = true;

            if (!moved) return;

            Diag.Info("perm",
                $"install={install} notifications={notifications} batteryExempt={battery}");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static void ReadAll(out bool canInstall, out bool notifications, out bool batteryExempt)
        {
            var context = global::Android.App.Application.Context;

            canInstall = true;
            notifications = true;
            batteryExempt = true;

            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    canInstall = context.PackageManager?.CanRequestPackageInstalls() ?? false;
            }
            catch (Exception ex) { Diag.Warn("perm", $"install check failed: {ex.Message}"); }

            try
            {
                // NotificationManager rather than NotificationManagerCompat: available from
                // API 24, minSdk here is 26, and it avoids a dependency for one call.
                if (context.GetSystemService(Context.NotificationService)
                    is global::Android.App.NotificationManager manager)
                    notifications = manager.AreNotificationsEnabled();
            }
            catch (Exception ex) { Diag.Warn("perm", $"notification check failed: {ex.Message}"); }

            try
            {
                if (context.GetSystemService(Context.PowerService) is PowerManager power)
                    batteryExempt = power.IsIgnoringBatteryOptimizations(context.PackageName!);
            }
            catch (Exception ex) { Diag.Warn("perm", $"battery check failed: {ex.Message}"); }
        }

        public Task<bool> RequestAsync(NoticeKind kind)
        {
            var context = global::Android.App.Application.Context;
            var package = context.PackageName!;

            Intent? intent = kind switch
            {
                NoticeKind.InstallPermission when OperatingSystem.IsAndroidVersionAtLeast(26) =>
                    new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                               global::Android.Net.Uri.Parse("package:" + package)),

                // The app-notification screen rather than a runtime request: once the user
                // has refused POST_NOTIFICATIONS twice Android stops showing the dialog at
                // all, and a button that does nothing is worse than one that opens Settings.
                NoticeKind.Notifications =>
                    new Intent(global::Android.Provider.Settings.ActionAppNotificationSettings)
                        .PutExtra(global::Android.Provider.Settings.ExtraAppPackage, package),

                NoticeKind.BatteryOptimisation =>
                    new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                               global::Android.Net.Uri.Parse("package:" + package)),

                _ => null
            };

            if (intent is null) return Task.FromResult(false);

            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                if (activity is not null)
                {
                    activity.StartActivity(intent);
                }
                else
                {
                    intent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(intent);
                }

                Diag.User("perm", $"opened settings for {kind}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                // Some OEM builds ship without these screens. Saying so is better than a
                // button that appears to do nothing.
                Diag.Warn("perm", $"could not open settings for {kind}: {ex.Message}");
                return Task.FromResult(false);
            }
        }
    }
}
