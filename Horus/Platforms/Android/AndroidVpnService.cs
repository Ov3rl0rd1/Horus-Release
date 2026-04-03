using Android.App;
using Android.Net;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Android
{
    [Service(Name = "com.app.vpn.VpnTunnelService", Permission = "android.permission.BIND_VPN_SERVICE")]
    public class AndroidVpnService : VpnService, IVpnPlatformService
    {
        public bool IsSupported => true;

        public TunnelState CurrentState => throw new NotImplementedException();

        public event EventHandler<TunnelStateChangedEventArgs> TunnelStateChanged;

        public Task ApplyRoutingRulesAsync(IEnumerable<RoutingRule> rules)
        {
            throw new NotImplementedException();
        }

        // StartTunnelAsync → Builder.Establish() → ParcelFileDescriptor (tun fd)
        // После этого Hysteria2 пишет в socks5/http proxy, платформа пишет в tun fd
        // ApplyRoutingRulesAsync → Builder.AddRoute() / Builder.ExcludeRoute() + AddDisallowedApplication()
        // SetDnsAsync → Builder.AddDnsServer()
        // Foreground service + Notification (required Android 8+)

        public Task<bool> RequestPermissionsAsync()   // Activity.StartActivityForResult(Prepare())
        {
            throw new NotImplementedException();
        }

        public Task SetDnsAsync(string[] dnsServers)
        {
            throw new NotImplementedException();
        }

        public Task StartTunnelAsync(TunnelOptions options, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task StopTunnelAsync()
        {
            throw new NotImplementedException();
        }
    }
}
