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
    /// <para><b>And why it often is not silent anyway.</b> That contract is one Android
    /// documents but OEM builds decline to honour — MIUI in particular asks for
    /// confirmation regardless. The session then completes with
    /// <c>STATUS_PENDING_USER_ACTION</c> instead of installing, which is handled in
    /// <see cref="UpdateInstallReceiver"/>. Nothing here may assume the quiet path.</para>
    ///
    /// <para><b>The one thing the user must do once.</b>
    /// <c>REQUEST_INSTALL_PACKAGES</c> is an app-op the user grants per app ("Install
    /// unknown apps"). Sideloading the first APK grants it to the browser or file manager,
    /// not to Horus. That is reported through <see cref="CheckReadiness"/> and surfaced as
    /// a Home screen notice, rather than asked for from here: a background app cannot
    /// reliably open a settings screen, and the previous attempt to do so failed silently.</para>
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

        public bool IsSupported => true;

        /// <summary>Always true — Android stops the process to replace it.</summary>
        public bool TerminatesProcess => true;

        /// <summary>
        /// False. The VpnService goes down with the process and the system reclaims the
        /// interface and its routes, so there is nothing here that needs unwinding first —
        /// unlike Windows, where a half-replaced install leaves a live wintun adapter.
        ///
        /// This used to be unconditional, and it was the whole bug: the tunnel was stopped,
        /// the install then needed a confirmation the app could not raise from the
        /// background, and the VPN stayed off while the attempt repeated every two minutes.
        /// </summary>
        public bool RequiresTunnelDown => false;

        /// <summary>
        /// Cheap and side-effect free, and called before anything is torn down.
        ///
        /// Only the app-op is checkable in advance. Whether the platform will demand a
        /// confirmation dialog is not knowable until the session is committed — MIUI and
        /// other OEM builds decline to honour <c>USER_ACTION_NOT_REQUIRED</c> — so that case
        /// is handled after the fact, in <see cref="UpdateInstallReceiver"/>.
        /// </summary>
        public UpdateBlocker CheckReadiness() =>
            CanInstallPackages(global::Android.App.Application.Context)
                ? UpdateBlocker.None
                : UpdateBlocker.InstallPermission;

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

            // CheckReadiness has already run before the caller committed to anything; this
            // is the belt-and-braces case where it changed underneath us.
            if (!CanInstallPackages(context))
                throw new InvalidOperationException(
                    "«Установка неизвестных приложений» не разрешена для Horus.");

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
    }
}
