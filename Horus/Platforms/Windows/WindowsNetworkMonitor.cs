using System.Net.NetworkInformation;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Change = Horus.Platforms.Windows.Tunnel.NetworkPathSelector.Change;

namespace Horus.Platforms.Windows
{
    /// <summary>A change of the path to the internet, as the Windows tunnel sees it.</summary>
    public sealed record NetworkPathChange(NetworkPath? Before, NetworkPath? After, Change Kind);

    /// <summary>
    /// Watches the path this machine's traffic takes to the internet.
    ///
    /// <para><b>What changed from the previous version, and why it mattered.</b> The old
    /// monitor hashed the set of every adapter that was up with a gateway, and reported a
    /// "handover" whenever that set changed while online. Windows changes that set all the
    /// time without the path changing at all: a second adapter flapping, a phone tethered
    /// for charging, a virtual switch from Hyper-V or WSL. Each "handover" reset the core's
    /// pooled sessions, and on Hysteria2 that drops every TCP connection it carries
    /// (confirmed on the bench: 4 resets, 16 of 16 long-lived sessions broken). Now only
    /// the default-route path is compared — the same choice the core binds its sockets
    /// to — and only its loss is a handover. See <see cref="NetworkPathSelector"/>.</para>
    ///
    /// <para>Our own adapter is excluded by name; Windows fires a burst of address events for
    /// one physical change, so they are debounced into one look.</para>
    /// </summary>
    public sealed class WindowsNetworkMonitor : INetworkMonitor
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

        private readonly object _sync = new();
        private CancellationTokenSource? _debounceCts;
        private NetworkPath? _path;
        private bool _started;

        public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;

        /// <summary>The Windows tunnel's own signal, with what the change means for open connections.</summary>
        public event EventHandler<NetworkPathChange>? PathChanged;

        /// <summary>Never raised: Windows has no platform validation of a VPN to push a verdict.</summary>
        public event EventHandler<string>? TunnelSuspect { add { } remove { } }

        /// <summary>Not raised: resuming from sleep arrives as an address change, already covered.</summary>
        public event EventHandler? DeviceWoke { add { } remove { } }

        /// <summary>Never raised: a desktop has no Doze.</summary>
        public event EventHandler<bool>? DeviceIdleChanged { add { } remove { } }

        public bool IsOnline { get; private set; }
        public NetworkTransport Transport { get; private set; } = NetworkTransport.None;

        /// <summary>The path the core is (or would be) bound to.</summary>
        public NetworkPath? CurrentPath
        {
            get { lock (_sync) return _path; }
        }

        public void ReportTunnelSuspect() { }

        public void Start()
        {
            if (_started) return;
            _started = true;

            NetworkChange.NetworkAddressChanged += OnChanged;
            NetworkChange.NetworkAvailabilityChanged += OnChanged;

            var path = ReadPath();
            lock (_sync) _path = path;
            IsOnline = path is not null;
            Transport = TransportOf(path);
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;

            NetworkChange.NetworkAddressChanged -= OnChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnChanged;
            _debounceCts?.Cancel();
        }

        /// <summary>Re-reads the path now, without waiting for an event.</summary>
        public NetworkPath? Refresh()
        {
            Settle();
            return CurrentPath;
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            CancellationToken token;
            lock (_sync)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                token = _debounceCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Debounce, token).ConfigureAwait(false);
                    Settle();
                }
                catch (OperationCanceledException) { /* superseded by a later event */ }
                catch (Exception ex) { Diag.Warn("net", $"network change not read: {ex.Message}"); }
            }, token);
        }

        private void Settle()
        {
            var routes = ReadRoutes();
            var after = NetworkPathSelector.Select(routes);

            NetworkPath? before;
            lock (_sync)
            {
                before = _path;
                _path = after;
            }

            var stillUp = before is not null && routes.Any(r =>
                r.InterfaceIndex == before.InterfaceIndex && r.IsUp && !r.IsTunnel);

            var kind = NetworkPathSelector.Classify(before, after, stillUp);
            if (kind == Change.None) return;

            IsOnline = after is not null;
            Transport = TransportOf(after);

            Diag.Info("net", $"path {kind}: {before?.Describe() ?? "none"} -> {after?.Describe() ?? "none"}");

            PathChanged?.Invoke(this, new NetworkPathChange(before, after, kind));

            // The shared event, for anything not specific to this platform. "Handover" means
            // what it says now: the connections over the old path are gone.
            NetworkChanged?.Invoke(this, new NetworkChangedEventArgs(
                Transport, IsOnline, IsHandover: kind is Change.Replaced or Change.Restored));
        }

        private static IReadOnlyList<DefaultRoute> ReadRoutes()
        {
            try { return RouteTable.DefaultRoutes(WindowsTunnelConfig.AdapterName); }
            catch (Exception ex)
            {
                Diag.Warn("net", $"route table unreadable: {ex.Message}");
                return [];
            }
        }

        private static NetworkPath? ReadPath() => NetworkPathSelector.Select(ReadRoutes());

        private static NetworkTransport TransportOf(NetworkPath? path) =>
            path is null ? NetworkTransport.None : path.IsWifi ? NetworkTransport.Wifi : NetworkTransport.Ethernet;
    }
}
