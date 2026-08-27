using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// The OS-level switches that silently disable features, and the screens that flip them.
    ///
    /// <para><b>Why this is a snapshot with an explicit refresh rather than live
    /// properties.</b> None of these can be observed as they change — Android has no
    /// broadcast for "the user granted install permission". The only honest signal is that
    /// the user left the app, went to Settings, and came back, which arrives as a window
    /// resume. So the state is re-read at exactly that moment and nowhere else: no timer,
    /// no polling, and nothing running while the screen is off.</para>
    /// </summary>
    public interface ISystemPermissions
    {
        /// <summary>
        /// Whether the app may install an APK. False means a downloaded update can never be
        /// applied — the updater must not start one, and above all must not take the tunnel
        /// down for one.
        /// </summary>
        bool CanInstallPackages { get; }

        /// <summary>Whether anything the app posts will actually be shown.</summary>
        bool NotificationsEnabled { get; }

        /// <summary>
        /// Whether the app is exempt from battery optimisation. Without it Doze suspends the
        /// app's network in its idle windows and long-lived tunnels die overnight.
        /// </summary>
        bool IgnoringBatteryOptimisations { get; }

        /// <summary>
        /// Raised when a re-read found something different. Consumers use it instead of
        /// asking repeatedly.
        /// </summary>
        event EventHandler? Changed;

        /// <summary>
        /// Re-reads everything and raises <see cref="Changed"/> if anything moved. Cheap —
        /// three synchronous system queries — and called only on resume.
        /// </summary>
        void Refresh();

        /// <summary>
        /// Opens the system screen that resolves <paramref name="kind"/>, or does the ask
        /// directly where the platform allows it. Returns false when there is no such screen
        /// on this device, which some OEM builds are.
        /// </summary>
        Task<bool> RequestAsync(NoticeKind kind);
    }
}
