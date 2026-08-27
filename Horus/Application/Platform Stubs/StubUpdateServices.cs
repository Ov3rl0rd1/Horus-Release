using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application.PlatformStubs
{
    /// <summary>
    /// Self-update is a Windows and Android feature. iOS and macOS get their updates from
    /// the store, and there is no sideload path to replace. Reporting
    /// <see cref="IsSupported"/> false makes <c>UpdateService.Start</c> a no-op rather than
    /// leaving a loop running that can never do anything.
    /// </summary>
    public sealed class StubUpdateInstaller : IUpdateInstaller
    {
        public bool IsSupported => false;
        public string? AssetSuffix => null;
        public bool TerminatesProcess => false;
        public bool RequiresTunnelDown => false;

        public UpdateBlocker CheckReadiness() => UpdateBlocker.PlatformRefused;

        public Task InstallAsync(string payloadPath, AppVersion version, CancellationToken ct) =>
            throw new NotSupportedException("Self-update is not available on this platform.");
    }

    public sealed class StubDeviceConditions : IDeviceConditions
    {
        public DeviceConditions Read() => DeviceConditions.Unknown;
    }

    public sealed class StubUserNotifier : IUserNotifier
    {
        public Task NotifyAsync(string title, string message) => Task.CompletedTask;
    }
}
