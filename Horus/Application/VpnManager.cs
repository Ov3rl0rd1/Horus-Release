using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Protocols;

namespace Horus.Application
{
    public class VpnManager
    {
        public event EventHandler<VpnStateChangedEventArgs>? StateChanged;
        public event EventHandler<ConnectionErrorEventArgs>? ConnectionError;
        public event EventHandler<ServerChangedEventArgs>? ServerChanged;
        public event EventHandler<ProtocolFallbackEventArgs>? ProtocolFallback;

        /// <summary>Raw stdout/stderr lines from the active protocol binary.</summary>
        public event EventHandler<string>? ProtocolOutputReceived;

        private readonly IVpnPlatformService _platform;
        private readonly ProtocolFactory _protocolFactory;
        private readonly IRoutingService _routing;
        private readonly ITrafficMonitorService _traffic;
        private readonly IApiService _api;
        private readonly IAuthService _auth;
        private readonly IBinaryUpdaterService _updater;
        private readonly IErrorReportingService _errorReporting;

        // Protocol fallback order: try Hysteria2 first, then OlcRtc
        private static readonly ProtocolType[] FallbackOrder =
            [ProtocolType.Hysteria2, ProtocolType.OlcRtc];

        public VpnState State { get; private set; } = VpnState.Disconnected;
        public IVpnProtocol? ActiveProtocol { get; private set; }
        public ServerInfo? ActiveServer { get; private set; }
        public string? LastProtocolLog { get; private set; }

        private readonly System.Text.StringBuilder _protocolLogBuffer = new();

        public VpnManager(
            IVpnPlatformService platform,
            ProtocolFactory protocolFactory,
            IRoutingService routing,
            ITrafficMonitorService traffic,
            IApiService api,
            IAuthService auth,
            IBinaryUpdaterService updater,
            IErrorReportingService errorReporting)
        {
            _platform = platform;
            _protocolFactory = protocolFactory;
            _routing = routing;
            _traffic = traffic;
            _api = api;
            _auth = auth;
            _updater = updater;
            _errorReporting = errorReporting;
        }

        public async Task ConnectAsync(ServerInfo server, CancellationToken ct = default)
        {
            if (State != VpnState.Disconnected) return;

            SetState(VpnState.Connecting, null);
            _protocolLogBuffer.Clear();

            try
            {
                var yamlConfig = await _api.GetServerConfigAsync(server.Id);

                bool granted = await _platform.RequestPermissionsAsync();
                if (!granted)
                    throw new InvalidOperationException("VPN permission denied by user.");

                Exception? lastEx = null;
                IVpnProtocol? connectedProtocol = null;

                foreach (var protocolType in FallbackOrder)
                {
                    try
                    {
                        var config = BuildConfig(protocolType, server, yamlConfig);
                        var protocol = _protocolFactory.Create(protocolType);
                        protocol.ErrorOccurred += OnProtocolError;
                        protocol.StatisticsUpdated += OnProtocolStats;
                        protocol.OutputReceived += OnProtocolOutput;

                        await protocol.ConnectAsync(config, ct);
                        connectedProtocol = protocol;

                        if (lastEx != null)
                        {
                            // We fell back to this protocol
                            ProtocolFallback?.Invoke(this, new ProtocolFallbackEventArgs(
                                FallbackOrder[Array.IndexOf(FallbackOrder, protocolType) - 1].ToString(),
                                protocolType.ToString(),
                                lastEx.Message));
                        }

                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        lastEx = ex;
                        var protocolLog = _protocolLogBuffer.ToString();
                        _errorReporting.RecordConnectionFailure(
                            protocolType.ToString(), ex.Message, protocolLog);

                        // Check if this was the last protocol to try
                        if (Array.IndexOf(FallbackOrder, protocolType) == FallbackOrder.Length - 1)
                        {
                            // All protocols failed — collect logs and attempt to send report
                            LastProtocolLog = protocolLog;
                            await TrySendFailureReportAsync();
                            throw new AggregateException(
                                "All VPN protocols failed. Please check logs.", ex);
                        }

                        // Try next protocol in fallback chain
                        _protocolLogBuffer.Clear();
                    }
                }

                if (connectedProtocol == null)
                    throw new InvalidOperationException("No protocol could connect.");

                ActiveProtocol = connectedProtocol;

                // Establish TUN, apply routing
                var tunnelOptions = BuildTunnelOptions(server);
                await _platform.StartTunnelAsync(tunnelOptions, ct);

                await _routing.RefreshFromServerAsync(ct);
                await _routing.ApplyAsync();

                if (_traffic is TrafficMonitorService tms) tms.Reset();
                _traffic.Start();

                var previous = ActiveServer;
                ActiveServer = server;
                if (previous != null)
                    ServerChanged?.Invoke(this, new ServerChangedEventArgs(previous, server));

                SetState(VpnState.Connected, null);
            }
            catch (OperationCanceledException)
            {
                SetState(VpnState.Disconnected, "Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                SetState(VpnState.Disconnected, ex.Message);
                ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(
                    ActiveProtocol?.Type.ToString() ?? "Unknown", ex.Message, false));
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            if (State == VpnState.Disconnected) return;

            SetState(VpnState.Disconnecting, null);
            try
            {
                _traffic.Stop();
                await _platform.StopTunnelAsync();

                if (ActiveProtocol != null)
                {
                    ActiveProtocol.StatisticsUpdated -= OnProtocolStats;
                    ActiveProtocol.ErrorOccurred -= OnProtocolError;
                    ActiveProtocol.OutputReceived -= OnProtocolOutput;
                    await ActiveProtocol.DisconnectAsync();
                    ActiveProtocol = null;
                }

                ActiveServer = null;
                SetState(VpnState.Disconnected, null);
            }
            catch (Exception ex)
            {
                SetState(VpnState.Error, ex.Message);
                throw;
            }
        }

        public async Task ReconnectAsync()
        {
            var server = ActiveServer;
            await DisconnectAsync();
            if (server != null)
                await ConnectAsync(server);
        }

        public async Task SwitchProtocolAsync(ProtocolType type, ProtocolConfig config)
        {
            var server = ActiveServer;
            await DisconnectAsync();
            if (server != null)
                await ConnectAsync(server);
        }

        /// <summary>
        /// Checks for a Hysteria2 binary update from GitHub Releases and installs it
        /// if available. Safe to call in background at startup.
        /// </summary>
        public async Task CheckAndUpdateBinariesAsync(CancellationToken ct = default)
        {
            try
            {
                var info = await _updater.CheckForUpdateAsync("hysteria2", ct);
                if (info.UpdateAvailable && State == VpnState.Disconnected)
                    await _updater.DownloadAndInstallAsync(info, ct: ct);
            }
            catch
            {
                // Non-fatal: use bundled binary
            }
        }

        // ── Event handlers ──────────────────────────────────────────────────

        private void OnProtocolStats(object? sender, TrafficStatisticsEventArgs e)
        {
            if (_traffic is TrafficMonitorService tms)
                tms.Feed(e);
        }

        private void OnProtocolError(object? sender, ProtocolErrorEventArgs e)
        {
            var line = $"[{DateTime.UtcNow:HH:mm:ss}] [{e.Code}] {e.Message}";
            _protocolLogBuffer.AppendLine(line);
            ProtocolOutputReceived?.Invoke(this, line);
            _errorReporting.RecordException(
                new Exception(e.Message),
                $"Protocol/{sender?.GetType().Name}/{e.Code}");
        }

        private void OnProtocolOutput(object? sender, string line)
        {
            _protocolLogBuffer.AppendLine(line);
            ProtocolOutputReceived?.Invoke(this, line);
        }

        // ── Private helpers ─────────────────────────────────────────────────

        private static ProtocolConfig BuildConfig(ProtocolType type, ServerInfo server, string renderedYaml) =>
            type switch
            {
                ProtocolType.Hysteria2 => new Hysteria2Config
                {
                    ServerId = server.Id.ToString(),
                    Name = server.Name,
                    RenderedConfig = renderedYaml,
                    Socks5Address = "127.0.0.1:1080"
                },
                ProtocolType.OlcRtc => new OlcRtcConfig
                {
                    ServerId = server.Id.ToString(),
                    Name = server.Name,
                    SignalServer = server.Host,
                    Socks5Address = "127.0.0.1:1081"
                },
                _ => throw new NotSupportedException(type.ToString())
            };

        private static TunnelOptions BuildTunnelOptions(ServerInfo server) => new()
        {
            // Must match HevSocksTunnel.HEV_SOCKS5_TUNNEL_CONFIG → tunnel.ipv4: 198.18.0.1
            // and the HorusVpnTunnelService.Builder.AddAddress call.
            TunAddress = "198.18.0.1",
            TunPrefix = 30,
            Mtu = 8500,       // matches HevSocksTunnel mtu: 8500
            DnsServers = ["1.1.1.1", "8.8.8.8"],
            BypassApps = [],
            AllTraffic = true
        };

        private async Task TrySendFailureReportAsync()
        {
            try
            {
                var sent = await _errorReporting.FlushAsync();
                if (!sent)
                    ConnectionError?.Invoke(this, new ConnectionErrorEventArgs(
                        "Reporting", "Could not send error report to server.", false));
            }
            catch { /* non-fatal */ }
        }

        private void SetState(VpnState newState, string? reason)
        {
            var old = State;
            State = newState;
            StateChanged?.Invoke(this, new VpnStateChangedEventArgs(old, newState, reason));
        }
    }
}
