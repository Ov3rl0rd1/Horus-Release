using Android.App;
using Android.Content;
using Horus.Domain.Interfaces;

namespace Horus.Platforms.Android.Update
{
    /// <summary>
    /// One low-importance notification on its own channel.
    ///
    /// <see cref="NotificationImportance.Low"/> is the whole point: it appears in the shade
    /// with no sound, no vibration and no heads-up banner. The product decision is that an
    /// update never interrupts, and a channel the user can mute separately from the VPN
    /// status notification is what makes that promise keepable.
    /// </summary>
    public sealed class AndroidUserNotifier : IUserNotifier
    {
        private const string ChannelId = "horus_updates";
        private const int NotificationId = 43;

        /// <summary>Separate id so a progress bar never replaces a finished-update message.</summary>
        private const int ProgressNotificationId = 45;

        /// <summary>
        /// An ongoing, low-importance progress notification.
        ///
        /// <c>SetOngoing</c> so it cannot be swiped away mid-download and reappear on the
        /// next update — a row that keeps coming back reads as a bug. It is removed by
        /// <see cref="HideProgressAsync"/> the moment the download ends, whichever way it
        /// ended.
        /// </summary>
        public Task ShowProgressAsync(string title, string message, int percent)
        {
            try
            {
                var manager = EnsureChannel();
                if (manager is null) return Task.CompletedTask;

                var builder = new Notification.Builder(
                        global::Android.App.Application.Context, ChannelId)
                    .SetContentTitle(title)
                    .SetContentText(message)
                    .SetSmallIcon(Resource.Drawable.appicon_notif)
                    .SetOngoing(true)
                    .SetOnlyAlertOnce(true)
                    .SetProgress(100, percent < 0 ? 0 : Math.Clamp(percent, 0, 100), percent < 0);

                if (BuildOpenAppIntent() is { } tap) builder.SetContentIntent(tap);

                manager.Notify(ProgressNotificationId, builder.Build());
            }
            catch { /* a notification is never worth an exception */ }

            return Task.CompletedTask;
        }

        public Task HideProgressAsync()
        {
            try
            {
                if (global::Android.App.Application.Context
                        .GetSystemService(Context.NotificationService) is NotificationManager manager)
                    manager.Cancel(ProgressNotificationId);
            }
            catch { }

            return Task.CompletedTask;
        }

        public Task NotifyAsync(string title, string message)
        {
            try
            {
                var context = global::Android.App.Application.Context;
                if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
                    return Task.CompletedTask;

                if (manager.GetNotificationChannel(ChannelId) is null)
                {
                    manager.CreateNotificationChannel(new NotificationChannel(
                        ChannelId, "Обновления", NotificationImportance.Low)
                    {
                        Description = "Тихие сообщения об обновлении приложения"
                    });
                }

                var builder = new Notification.Builder(context, ChannelId)
                    .SetContentTitle(title)
                    .SetContentText(message)
                    .SetSmallIcon(Resource.Drawable.appicon_notif)
                    .SetAutoCancel(true);

                if (BuildOpenAppIntent() is { } tap) builder.SetContentIntent(tap);

                manager.Notify(NotificationId, builder.Build());
            }
            catch { /* a notification is never worth an exception */ }

            return Task.CompletedTask;
        }

        /// <summary>Creates the channel on first use and hands back the manager.</summary>
        private static NotificationManager? EnsureChannel()
        {
            var context = global::Android.App.Application.Context;
            if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
                return null;

            if (manager.GetNotificationChannel(ChannelId) is null)
            {
                manager.CreateNotificationChannel(new NotificationChannel(
                    ChannelId, "Обновления", NotificationImportance.Low)
                {
                    Description = "Тихие сообщения об обновлении приложения"
                });
            }

            return manager;
        }

        /// <summary>
        /// Brings the app to the front when the notification is tapped. Without a content
        /// intent the row is inert, which for the one place the app speaks from the
        /// background is a wasted affordance.
        /// </summary>
        private static PendingIntent? BuildOpenAppIntent()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
                if (launch is null) return null;

                launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);

                return PendingIntent.GetActivity(context, 0, launch,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            }
            catch { return null; }
        }
    }
}
