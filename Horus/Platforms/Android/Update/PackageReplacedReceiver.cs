using Android.App;
using Android.Content;
using Horus.Application.Update;
using Horus.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Horus.Platforms.Android.Update
{
    /// <summary>
    /// Brings the tunnel back after the app updates itself.
    ///
    /// Installing an APK stops the process, and Android does not restart an app afterwards.
    /// Without this a user whose VPN had been on for weeks would silently lose it to a
    /// background update — which would make the updater a bigger reliability problem than
    /// the one it was built to solve. <c>ACTION_MY_PACKAGE_REPLACED</c> is delivered to the
    /// new build immediately after the swap, which is the one moment we can act on.
    ///
    /// Whether the reconnect is permitted depends on the foreground-service restrictions:
    /// an app excluded from battery optimisation may start one from the background, and
    /// one that is not, may not. Both outcomes are handled — the notification is not a
    /// nicety, it is the fallback for the case where the platform refuses.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter([Intent.ActionMyPackageReplaced])]
    public class PackageReplacedReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != Intent.ActionMyPackageReplaced) return;

            var shouldReconnect = Preferences.Get(UpdateService.ReconnectAfterUpdateKey, false);
            Preferences.Remove(UpdateService.ReconnectAfterUpdateKey);

            var notifier = IPlatformApplication.Current?.Services?.GetService<IUserNotifier>();

            if (!shouldReconnect)
            {
                _ = notifier?.NotifyAsync("Horus обновлён", $"Версия {AppConfiguration.AppVersion}");
                return;
            }

            // PendingResult keeps the receiver alive past OnReceive; a connect involves a
            // network round trip and would otherwise be killed mid-flight.
            var pending = GoAsync();
            _ = Task.Run(async () =>
            {
                var ok = await BackgroundVpnControl.TryConnectAsync().ConfigureAwait(false);

                if (notifier is not null)
                {
                    await notifier.NotifyAsync(
                        "Horus обновлён",
                        ok
                            ? $"Версия {AppConfiguration.AppVersion}. VPN снова включён."
                            : $"Версия {AppConfiguration.AppVersion}. Нажмите, чтобы включить VPN.")
                        .ConfigureAwait(false);
                }

                pending?.Finish();
            });
        }
    }
}
