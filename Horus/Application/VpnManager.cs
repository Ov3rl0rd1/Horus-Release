using Android.Widget;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Protocols;

namespace Horus.Application
{
    public partial class VpnManager
    {
        public event EventHandler<VpnStateChangedEventArgs> StateChanged;
        public event EventHandler<ConnectionErrorEventArgs> ConnectionError;
        public event EventHandler<ServerChangedEventArgs> ServerChanged;

        // Зависимости
        private readonly IVpnPlatformService _platform;
        private readonly ProtocolFactory _protocolFactory;
        private readonly IRoutingService _routing;
        private readonly IGeoDataService _geo;
        private readonly ITrafficMonitorService _traffic;
        private readonly ISubscriptionService _subscription;

        // State
        public VpnState State { get; private set; }
        public IVpnProtocol? ActiveProtocol { get; private set; }
        public ServerInfo? ActiveServer { get; private set; }

        // ─── Подключение ───────────────────────────────────────────────────────
        public Task ConnectAsync(ServerInfo server, ProtocolConfig config, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
        public Task DisconnectAsync()
        {
            throw new NotImplementedException();
        }
        public Task ReconnectAsync()   // при смене сервера/протокола
        {
            throw new NotImplementedException();
        }

        // ─── Смена протокола (главная точка гибкости) ──────────────────────────
        public Task SwitchProtocolAsync(ProtocolType type, ProtocolConfig config)
        {
            throw new NotImplementedException();
        }
        // Последовательность: Disconnect → factory.Create(type) → Connect

        // ─── События ───────────────────────────────────────────────────────────
    }
}
