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

                var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
                PendingIntent? tap = null;
                if (launch is not null)
                {
                    launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
                    tap = PendingIntent.GetActivity(context, 0, launch,
                        PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                }

                var builder = new Notification.Builder(context, ChannelId)
                    .SetContentTitle(title)
                    .SetContentText(message)
                    .SetSmallIcon(Resource.Drawable.appicon_notif)
                    .SetAutoCancel(true);

                if (tap is not null) builder.SetContentIntent(tap);

                manager.Notify(NotificationId, builder.Build());
            }
            catch { /* a notification is never worth an exception */ }

            return Task.CompletedTask;
        }
    }
}
