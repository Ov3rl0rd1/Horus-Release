using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application.PlatformStubs
{
    /// <summary>
    /// Everything permitted, nothing to ask for.
    ///
    /// Reporting "granted" is the right default rather than a cautious "denied": every
    /// consumer treats a false as a reason to show the user a banner, so a pessimistic stub
    /// would put three permanent, unresolvable notices on the Home screen of every desktop
    /// build. Windows has no equivalent of these switches — an elevated app installs an MSI
    /// without asking, and toasts are handled by the notifier itself.
    /// </summary>
    public sealed class StubSystemPermissions : ISystemPermissions
    {
        public event EventHandler? Changed;

        public bool CanInstallPackages => true;
        public bool NotificationsEnabled => true;
        public bool IgnoringBatteryOptimisations => true;

        public void Refresh() { }

        public Task<bool> RequestAsync(NoticeKind kind) => Task.FromResult(false);
    }
}
