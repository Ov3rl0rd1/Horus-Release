using Android.App;
using Android.Content;
using Android.Content.PM;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android.Update
{
    /// <summary>
    /// Installs an APK over this app through <see cref="PackageInstaller"/>.
    ///
    /// <para><b>Why this can be silent.</b> Android normally shows a confirmation for every
    /// sideloaded install, but <c>setRequireUserAction(USER_ACTION_NOT_REQUIRED)</c> is
    /// honoured when the installer <i>is updating itself</i>, the app targets a recent
    /// enough SDK, and the installer declares
    /// <c>UPDATE_PACKAGES_WITHOUT_USER_ACTION</c>. A self-update satisfies the
    /// installer-identity condition outright — the usual "must be the installer of record"
    /// requirement does not apply to it — so no prompt appears, from the very first update
    /// onward.</para>
    ///
    /// <para><b>The one thing the user must do once.</b>
    /// <c>REQUEST_INSTALL_PACKAGES</c> is an app-op the user grants per app ("Install
    /// unknown apps"). Sideloading the first APK grants it to the browser or file manager,
    /// not to Horus. Until the user grants it here, the install cannot even be attempted,
    /// so this asks for it once — quietly, through a notification rather than by hijacking
    /// the screen — and gives up until it is granted.</para>
    ///
    /// <para>The process does not survive: Android stops the app to swap it. Nothing may be
    /// assumed to run after <see cref="InstallAsync"/> commits.</para>
    /// </summary>
    public sealed class AndroidUpdateInstaller : IUpdateInstaller
    {
        /// <summary>
        /// <c>PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED</c>. Spelled out
        /// because the value is what the platform checks and the binding for the constant
        /// has moved between .NET Android releases.
        /// </summary>
        private const int UserActionNotRequired = 2;

        private readonly IUserNotifier _notifier;

        public AndroidUpdateInstaller(IUserNotifier notifier) => _notifier = notifier;

        public bool IsSupported => true;

        /// <summary>Always true — Android stops the process to replace it.</summary>
        public bool TerminatesProcess => true;

        /// <summary>
        /// The ABI this device actually runs. Releases ship one APK per architecture
        /// because the core is ~53 MB each, so picking the wrong one is a 60 MB download
        /// that cannot install.
        /// </summary>
        public string? AssetSuffix
        {
            get
            {
                var abis = global::Android.OS.Build.SupportedAbis;
                if (abis is null || abis.Count == 0) return null;

                foreach (var abi in abis)
                {
                    if (abi == "arm64-v8a") return "-android-arm64-v8a.apk";
                    if (abi == "x86_64") return "-android-x86_64.apk";
                }
                return null;
            }
        }

        public async Task InstallAsync(string payloadPath, AppVersion version, CancellationToken ct)
        {
            var context = global::Android.App.Application.Context;

            if (!CanInstallPackages(context))
            {
                await RequestInstallPermissionAsync(context).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "«Установка неизвестных приложений» не разрешена для Horus.");
            }

            var installer = context.PackageManager?.PackageInstaller
                ?? throw new InvalidOperationException("PackageInstaller unavailable.");

            var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
            parameters.SetAppPackageName(context.PackageName);

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                parameters.SetRequireUserAction(UserActionNotRequired);

            var sessionId = installer.CreateSession(parameters);
            using (var session = installer.OpenSession(sessionId))
            {
                var length = new FileInfo(payloadPath).Length;
                await using (var source = File.OpenRead(payloadPath))
                await using (var destination = session.OpenWrite("horus.apk", 0, length))
                {
                    await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                    destination.Flush();
                    session.Fsync(destination);
                }

                // Mutable: the system writes the status and, when it decides a prompt is
                // needed after all, the confirmation intent into this PendingIntent.
                var intent = new Intent(context, typeof(UpdateInstallReceiver))
                    .SetAction(UpdateInstallReceiver.ActionInstallStatus);

                var flags = PendingIntentFlags.UpdateCurrent;
                if (OperatingSystem.IsAndroidVersionAtLeast(31)) flags |= PendingIntentFlags.Mutable;

                var pending = PendingIntent.GetBroadcast(context, sessionId, intent, flags)
                    ?? throw new InvalidOperationException("Could not build the install callback.");

                session.Commit(pending.IntentSender);
            }
        }

        private static bool CanInstallPackages(Context context)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return true;
            return context.PackageManager?.CanRequestPackageInstalls() ?? false;
        }

        /// <summary>
        /// Sends the user to the one settings screen that can grant this. Deliberately a
        /// notification, not an activity launch: updating must never interrupt, and a
        /// background app cannot reliably start an activity anyway.
        /// </summary>
        private async Task RequestInstallPermissionAsync(Context context)
        {
            try
            {
                await _notifier.NotifyAsync(
                    "Horus не может обновиться сам",
                    "Разрешите установку приложений из Horus, чтобы обновления ставились автоматически.")
                    .ConfigureAwait(false);

                if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

                var intent = new Intent(
                    global::Android.Provider.Settings.ActionManageUnknownAppSources,
                    global::Android.Net.Uri.Parse("package:" + context.PackageName));
                intent.AddFlags(ActivityFlags.NewTask);

                // Only works while something of ours is in the foreground; failing here is
                // expected and harmless, since the notification already carries the ask.
                context.StartActivity(intent);
            }
            catch { }
        }
    }
}
