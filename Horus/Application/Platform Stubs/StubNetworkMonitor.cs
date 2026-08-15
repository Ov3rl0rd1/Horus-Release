using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application.PlatformStubs
{
    /// <summary>
    /// Reports a permanently online link and never raises a change.
    ///
    /// Claiming to be online is the right default for a platform with no implementation:
    /// the health monitor treats "no internet" as a reason to stop trying, so a stub that
    /// reported offline would silently disable recovery everywhere it is used.
    /// </summary>
    public sealed class StubNetworkMonitor : INetworkMonitor
    {
        public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;
        public event EventHandler? TunnelValidationLost;
        public event EventHandler? DeviceWoke;

        public bool IsOnline => true;
        public NetworkTransport Transport => NetworkTransport.Other;

        public void ReportTunnelSuspect() { }
        public void Start() { }
        public void Stop() { }
    }
}
