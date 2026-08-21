using System.Net.NetworkInformation;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Watches the physical adapters on Windows.
    ///
    /// <para>Two things make this noisier than the Android equivalent, and both are handled
    /// by ignoring rather than reacting. Our own wintun adapter appearing, getting its
    /// address and going away all raise <c>NetworkAddressChanged</c>, so the tunnel's own
    /// interface is excluded from the snapshot — otherwise every connect would look like a
    /// handover and trigger a probe against a tunnel that is still being built. And Windows
    /// fires several of these events in a burst for one physical change, so they are
    /// debounced into a single verdict.</para>
    ///
    /// <para>A "handover" here is a change in which adapters can reach a gateway, not any
    /// address change — a DHCP renewal on the same link is not worth a probe.</para>
    /// </summary>
    public sealed class WindowsNetworkMonitor : INetworkMonitor
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

        private readonly object _sync = new();
        private CancellationTokenSource? _debounceCts;
        private string _signature = string.Empty;
        private bool _started;

        public event EventHandler<NetworkChangedEventArgs>? NetworkChanged;

        /// <summary>
        /// Never raised on Windows: there is no equivalent of Android's continuous network
        /// validation or of ConnectivityDiagnosticsManager, so nothing here can push a
        /// "the tunnel stopped working" verdict. Detection on this platform stays with the
        /// counter checks in TunnelHealthMonitor.
        /// </summary>
        public event EventHandler<string>? TunnelSuspect;

        /// <summary>
        /// Not raised. Resuming from sleep brings the adapters back, and that already
        /// arrives as an address-availability change — so the wake case is covered by the
        /// subscription above without taking a dependency on
        /// <c>Microsoft.Win32.SystemEvents</c> for a second path to the same event.
        /// </summary>
        public event EventHandler? DeviceWoke;

        /// <summary>
        /// Never raised on Windows: there is no Doze, and a desktop that suspends stops
        /// executing rather than throttling, so there is no window in which pausing
        /// housekeeping would buy anything.
        /// </summary>
        public event EventHandler<bool>? DeviceIdleChanged;

        public bool IsOnline { get; private set; }
        public NetworkTransport Transport { get; private set; } = NetworkTransport.None;

        /// <summary>No platform revalidation to ask for; the health monitor probes instead.</summary>
        public void ReportTunnelSuspect() { }

        public void Start()
        {
            if (_started) return;
            _started = true;

            NetworkChange.NetworkAddressChanged += OnChanged;
            NetworkChange.NetworkAvailabilityChanged += OnChanged;
            Snapshot(out _signature, out var online, out var transport);
            IsOnline = online;
            Transport = transport;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;

            NetworkChange.NetworkAddressChanged -= OnChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnChanged;
            _debounceCts?.Cancel();
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
            }, token);
        }

        private void Settle()
        {
            Snapshot(out var signature, out var online, out var transport);
            if (signature == _signature) return;

            var wasOnline = IsOnline;
            _signature = signature;
            IsOnline = online;
            Transport = transport;

            NetworkChanged?.Invoke(this,
                new NetworkChangedEventArgs(transport, online, IsHandover: online && wasOnline));
        }

        /// <summary>
        /// Describes the usable adapters, excluding our own tunnel. The signature is what
        /// the change detection compares, so it has to contain everything that matters and
        /// nothing that flaps on its own.
        /// </summary>
        private static void Snapshot(out string signature, out bool online, out NetworkTransport transport)
        {
            signature = string.Empty;
            online = false;
            transport = NetworkTransport.None;

            try
            {
                var parts = new List<string>();
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    // Our own adapter, and anything else calling itself a tunnel.
                    if (nic.Description.Contains("Socks5 Tunnel", StringComparison.OrdinalIgnoreCase)) continue;
                    if (nic.Name.Contains("Horus", StringComparison.OrdinalIgnoreCase)) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    // A default gateway is the cheap test for "can actually reach anything".
                    var properties = nic.GetIPProperties();
                    if (properties.GatewayAddresses.Count == 0) continue;

                    parts.Add(nic.Id);
                    online = true;

                    if (transport == NetworkTransport.None)
                    {
                        transport = nic.NetworkInterfaceType switch
                        {
                            NetworkInterfaceType.Wireless80211 => NetworkTransport.Wifi,
                            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet =>
                                NetworkTransport.Ethernet,
                            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 =>
                                NetworkTransport.Cellular,
                            _ => NetworkTransport.Other
                        };
                    }
                }

                parts.Sort(StringComparer.Ordinal);
                signature = string.Join('|', parts);
            }
            catch
            {
                // Assume online: refusing to connect because the adapter list could not be
                // read would be a worse failure than a missed handover.
                online = true;
                transport = NetworkTransport.Other;
            }
        }
    }
}
