using Android.App;
using Android.Content;
using Horus.Application;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    /// <summary>
    /// Brings the tunnel back after a reboot or a profile unlock.
    ///
    /// <para><b>Every check here exists to stop it doing the wrong thing.</b> A boot
    /// receiver that starts a VPN unconditionally is worse than none: it fights Always-on
    /// VPN, it reconnects for a user who deliberately turned the VPN off, and it races the
    /// UI on a normal launch. The order below is taken from Rethink's BootStartWorker,
    /// where each condition maps to a class of complaint they had to fix.</para>
    ///
    /// <para><b>ACTION_LOCKED_BOOT_COMPLETED is deliberately not handled.</b> It arrives
    /// before the user has unlocked the device, when credential-encrypted storage — where
    /// <c>Preferences</c> lives — is not yet readable. Every decision this receiver makes
    /// depends on reading it, so acting that early would mean acting on defaults.
    /// ACTION_USER_UNLOCKED is the earliest moment the answer exists. NekoBox refuses the
    /// same event for the same reason.</para>
    ///
    /// <para>🔧 The connect runs straight from the receiver, held alive by
    /// <c>GoAsync</c>. Rethink hands this to WorkManager instead, which survives OEM boot
    /// killers better; if field reports show boot starts being dropped on particular
    /// vendors, that is the upgrade — it needs an AndroidX.Work binding, not a redesign.</para>
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
    [IntentFilter([
        Intent.ActionBootCompleted,
        Intent.ActionUserUnlocked])]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var action = intent?.Action;
            if (action is not (Intent.ActionBootCompleted or Intent.ActionUserUnlocked)) return;

            // (1) The user turned this off.
            if (!UserPreferences.AutoStartOnBoot)
            {
                Diag.Info("boot", $"{action}: auto-start disabled");
                return;
            }

            // (2) The VPN was not on when the device went down. Restoring it would be the
            //     app deciding something the user did not ask for.
            if (!VpnIntent.Active)
            {
                Diag.Info("boot", $"{action}: no active intent");
                return;
            }

            // (3) Always-on VPN is enabled: the system starts the service itself, earlier
            //     and more reliably than we can. Two mechanisms racing produces a double
            //     start, and the second one fails in ways that look like a bug.
            if (IsAlwaysOnEnabled(context))
            {
                Diag.Info("boot", $"{action}: always-on is enabled, leaving it to the system");
                return;
            }

            // (4) Consent is missing and there is no Activity here to ask with. Opening one
            //     from a boot broadcast is both restricted and hostile.
            if (!AndroidVpnService.HasConsent())
            {
                Diag.Warn("boot", $"{action}: no VPN consent, cannot start headless");
                return;
            }

            // (5) Something already got there — the UI on a cold start, or a duplicate
            //     broadcast. BOOT_COMPLETED and USER_UNLOCKED often both arrive.
            if (BackgroundVpnControl.IsConnected)
            {
                Diag.Info("boot", $"{action}: already connected");
                return;
            }

            Diag.Info("boot", $"{action}: restoring tunnel");

            // Keeps the receiver alive past OnReceive — a connect involves a network round
            // trip and would otherwise be killed mid-flight.
            var pending = GoAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await BackgroundVpnControl.TryConnectAsync().ConfigureAwait(false);
                    Diag.Info("boot", ok ? "tunnel restored" : "restore failed");
                }
                catch (Exception ex)
                {
                    Diag.Error("boot", $"restore threw: {ex.Message}");
                }
                finally
                {
                    pending?.Finish();
                }
            });
        }

        /// <summary>
        /// Whether the user has made Horus the always-on VPN.
        ///
        /// <para>Read through the raw <c>Settings.Secure</c> key because there is no public
        /// API for it. That makes it best-effort by nature: a vendor build may return null
        /// or refuse the read, and an unreadable answer must not disable the boot start —
        /// so a failure here reports false and the ordinary path continues.</para>
        /// </summary>
        internal static bool IsAlwaysOnEnabled(Context? context)
        {
            try
            {
                var ctx = context ?? global::Android.App.Application.Context;
                var resolver = ctx.ContentResolver;
                if (resolver is null) return false;

                var value = global::Android.Provider.Settings.Secure.GetString(resolver, "always_on_vpn_app");
                return !string.IsNullOrEmpty(value) &&
                       string.Equals(value, ctx.PackageName, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
