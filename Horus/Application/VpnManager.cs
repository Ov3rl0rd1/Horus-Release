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

        private readonly IVpnPlatformService _platform;
        private readonly ProtocolFactory _protocolFactory;
        private readonly IRoutingService _routing;
        private readonly ITrafficMonitorService _traffic;
        private readonly IApiService _api;
        private readonly IAuthService _auth;

        public VpnState State { get; private set; } = VpnState.Disconnected;
        public IVpnProtocol? ActiveProtocol { get; private set; }
        public ServerInfo? ActiveServer { get; private set; }

        public VpnManager(
            IVpnPlatformService platform,
            ProtocolFactory protocolFactory,
            IRoutingService routing,
            ITrafficMonitorService traffic,
            IApiService api,
            IAuthService auth)
        {
            _platform = platform;
            _protocolFactory = protocolFactory;
            _routing = routing;
            _traffic = traffic;
            _api = api;
            _auth = auth;
        }

        public async Task ConnectAsync(ServerInfo server, CancellationToken ct = default)
        {
            if (State != VpnState.Disconnected) return;

            SetState(VpnState.Connecting, null);

            try
            {
                // Refresh auth token before connecting
                if (_auth.IsAuthenticated)
                    await _auth.RefreshTokenAsync();

                // Fetch rendered Hysteria2 config from server
                var yamlConfig = await _api.GetServerConfigAsync(server.Id);

                var config = new Hysteria2Config
                {
                    ServerId = server.Id.ToString(),
                    Name = server.Name,
                    RenderedConfig = yamlConfig,
                    Socks5Address = "127.0.0.1:1080"
                };

                // Request VPN permissions (shows Android dialog if needed)
                bool granted = await _platform.RequestPermissionsAsync();
                if (!granted)
                    throw new InvalidOperationException("VPN permission denied.");

                // Start Hysteria2 protocol (launches binary, waits for SOCKS5 ready)
                var protocol = _protocolFactory.Create(ProtocolType.Hysteria2);
                protocol.StatisticsUpdated += OnProtocolStats;
                await protocol.ConnectAsync(config, ct);

                ActiveProtocol = protocol;

                // Establish TUN interface, route all traffic through SOCKS5
                var tunnelOptions = new TunnelOptions
                {
                    TunAddress = "10.8.0.1",
                    TunPrefix = 30,
                    Mtu = 1500,
                    DnsServers = ["1.1.1.1", "8.8.8.8"],
                    BypassApps = [],
                    AllTraffic = true
                };
                await _platform.StartTunnelAsync(tunnelOptions, ct);

                // Start traffic monitoring
                if (_traffic is TrafficMonitorService tms)
                    tms.Reset();
                _traffic.Start();

                var previous = ActiveServer;
                ActiveServer = server;
                if (previous != null)
                    ServerChanged?.Invoke(this, new ServerChangedEventArgs(previous, server));

                SetState(VpnState.Connected, null);
            }
            catch (Exception ex)
            {
                SetState(VpnState.Disconnected, ex.Message);
                ConnectionError?.Invoke(this, new ConnectionErrorEventArgs("Hysteria2", ex.Message, false));
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

        private void OnProtocolStats(object? sender, TrafficStatisticsEventArgs e)
        {
            if (_traffic is TrafficMonitorService tms)
                tms.Feed(e);
        }

        private void SetState(VpnState newState, string? reason)
        {
            var old = State;
            State = newState;
            StateChanged?.Invoke(this, new VpnStateChangedEventArgs(old, newState, reason));
        }
    }
}
