using Android.App;
using Android.Content;
using Android.Content.PM;
using Horus.Application;
using Horus.Application.Update;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Horus.Platforms.Android.Update
{
    /// <summary>
    /// Receives the outcome of a <see cref="PackageInstaller"/> session.
    ///
    /// <para><b>This is where the reported bug actually lived.</b> The session commits fine;
    /// the platform then answers <c>STATUS_PENDING_USER_ACTION</c> — MIUI and other OEM
    /// builds do not honour <c>USER_ACTION_NOT_REQUIRED</c> even for a self-update — and the
    /// previous code responded by calling <c>startActivity</c> straight from here. Android
    /// 10 forbids starting an activity from the background, so on a phone in the user's
    /// pocket that call did nothing at all, silently. The install never happened, the plan
    /// was never cleared, and the updater tried again every two minutes. The only way a user
    /// ever got the update was by opening the app first, which is exactly what was
    /// reported.</para>
    ///
    /// <para><b>The fix is to route through a notification when we are not in the
    /// foreground.</b> A pending intent fired from a notification tap is a user gesture, so
    /// the system allows the confirmation dialog to open. That is the supported way to ask
    /// for something while backgrounded, and it costs nothing until it is needed.</para>
    ///
    /// <para>Everything here also reports through <see cref="Diag"/>. Previously these went
    /// to a bare logcat tag that no diagnostics archive collected, which is why hours of
    /// failed installs left no trace at all.</para>
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class UpdateInstallReceiver : BroadcastReceiver
    {
        public const string ActionInstallStatus = "com.horus.vpn.INSTALL_STATUS";

        private const string ChannelId = "horus_updates";
        private const int PromptNotificationId = 44;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context is null || intent is null) return;

            var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);
            var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);

            switch (status)
            {
                case (int)PackageInstallStatus.PendingUserAction:
                    Diag.Info("update", "the system wants the user to confirm the install");
                    HandlePendingUserAction(context, intent);
                    break;

                case (int)PackageInstallStatus.Success:
                    // Nothing to do: the process is about to be replaced, and the "updated"
                    // notification is raised on the next launch from the recorded version.
                    Diag.Info("update", "install reported success");
                    break;

                default:
                    Diag.Error("update", $"install failed (status {status})", message);
                    Report(blocker: status == (int)PackageInstallStatus.FailureBlocked
                        ? UpdateBlocker.InstallPermission
                        : UpdateBlocker.PlatformRefused);
                    break;
            }
        }

        /// <summary>
        /// Opens the confirmation dialog if we can, and asks the user to open it if we
        /// cannot. The distinction is the foreground state: only a foreground app may start
        /// an activity, and pretending otherwise is what made this fail invisibly.
        /// </summary>
        private static void HandlePendingUserAction(Context context, Intent intent)
        {
            var confirm = ExtractConfirmIntent(intent);
            if (confirm is null)
            {
                Diag.Warn("update", "no confirmation intent in the status broadcast");
                Report(UpdateBlocker.PlatformRefused);
                return;
            }

            if (AppVisibility.IsForeground)
            {
                try
                {
                    confirm.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(confirm);
                    Diag.Info("update", "confirmation dialog opened");
                    return;
                }
                catch (Exception ex)
                {
                    Diag.Warn("update", $"could not open the confirmation dialog: {ex.Message}");
                }
            }

            PromptFromNotification(context, confirm);
        }

        /// <summary>
        /// Posts a notification whose tap opens the confirmation. Launching an activity from
        /// a notification tap is a user gesture, which the background-start restrictions
        /// allow — so this is the supported route, not a workaround.
        /// </summary>
        private static void PromptFromNotification(Context context, Intent confirm)
        {
            try
            {
                if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
                    return;

                if (manager.GetNotificationChannel(ChannelId) is null)
                {
                    manager.CreateNotificationChannel(new NotificationChannel(
                        ChannelId, "Обновления", NotificationImportance.Low)
                    {
                        Description = "Тихие сообщения об обновлении приложения"
                    });
                }

                confirm.AddFlags(ActivityFlags.NewTask);
                var tap = PendingIntent.GetActivity(
                    context, PromptNotificationId, confirm,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

                if (tap is null) return;

                var notification = new Notification.Builder(context, ChannelId)
                    .SetContentTitle("Обновление Horus готово")
                    .SetContentText("Нажмите, чтобы установить новую версию")
                    .SetSmallIcon(Resource.Drawable.appicon_notif)
                    .SetContentIntent(tap)
                    .SetAutoCancel(true)
                    .Build();

                manager.Notify(PromptNotificationId, notification);
                Diag.Info("update", "asked for confirmation through a notification");
            }
            catch (Exception ex)
            {
                // If notifications are switched off this is where it dies — which is why
                // "уведомления выключены" is one of the Home screen notices.
                Diag.Warn("update", $"could not post the install prompt: {ex.Message}");
                Report(UpdateBlocker.PlatformRefused);
            }
        }

        private static Intent? ExtractConfirmIntent(Intent intent)
        {
            try
            {
                return OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent
                    : intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
            }
            catch (Exception ex)
            {
                Diag.Warn("update", $"could not read the confirmation intent: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Tells the updater what happened, so it backs off instead of retrying every two
        /// minutes and so the Home screen can explain itself.
        /// </summary>
        private static void Report(UpdateBlocker blocker)
        {
            try
            {
                if (IPlatformApplication.Current?.Services?.GetService<IUpdateService>()
                    is UpdateService service)
                    service.NoteInstallFailure(blocker);
            }
            catch (Exception ex)
            {
                Diag.Warn("update", $"could not report the install result: {ex.Message}");
            }
        }
    }
}
