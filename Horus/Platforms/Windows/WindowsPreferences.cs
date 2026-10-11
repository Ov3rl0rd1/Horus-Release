using Horus.Application;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// Settings only the Windows tunnel has. Kept apart from the shared
    /// <see cref="UserPreferences"/> so neither platform's options leak into the other's
    /// screens.
    /// </summary>
    public static class WindowsPreferences
    {
        private const string SpeedModeKey = "win.tun.speedmode";
        private const string Ipv6Key = "win.tun.ipv6";
        private const string KillSwitchKey = "win.killswitch";
        private const string GameUdpKey = "win.game.udp";

        /// <summary>
        /// Large frames on the TUN (MTU 9000) instead of 1500. Measured on the bench: 2.4×
        /// the bulk throughput, at the cost of a game's p99 latency during a concurrent
        /// download (12 ms against 7.9 ms). Off by default — this is a gaming client.
        /// </summary>
        public static bool SpeedMode
        {
            get => Get(SpeedModeKey, false);
            set => Set(SpeedModeKey, value);
        }

        /// <summary>Route IPv6 into the tunnel too; off sends it past the VPN.</summary>
        public static bool CaptureIpv6
        {
            get => Get(Ipv6Key, true);
            set => Set(Ipv6Key, value);
        }

        /// <summary>
        /// Keep the tunnel up — and the internet blocked — when the VPN cannot recover,
        /// rather than dropping back to the direct connection after a few minutes.
        /// </summary>
        public static bool KillSwitch
        {
            get => Get(KillSwitchKey, false);
            set => Set(KillSwitchKey, value);
        }

        /// <summary>
        /// Prefer a UDP-native transport (Hysteria2) when the node offers one: game traffic
        /// is UDP, and over VLESS it rides inside TCP, where one lost segment holds up every
        /// packet behind it. On by default.
        /// </summary>
        public static bool PreferUdpTransport
        {
            get => Get(GameUdpKey, true);
            set => Set(GameUdpKey, value);
        }

        public static WindowsTunSettings TunSettings => new(
            Mtu: SpeedMode ? WindowsTunnelConfig.SpeedMtu : WindowsTunnelConfig.DefaultMtu,
            CaptureIpv6: CaptureIpv6);

        /// <summary>
        /// The core's log level. "warning" unless verbose logging is on: at "info" the core
        /// writes several lines per connection, which in a long session with a browser open
        /// is constant disk I/O for a log nobody reads.
        /// </summary>
        public static string CoreLogLevel => UserPreferences.VerboseLogging ? "debug" : "warning";

        public static IEnumerable<KeyValuePair<string, string?>> Describe()
        {
            yield return new("speedMode", SpeedMode.ToString());
            yield return new("captureIpv6", CaptureIpv6.ToString());
            yield return new("killSwitch", KillSwitch.ToString());
            yield return new("preferUdp", PreferUdpTransport.ToString());
        }

        private static bool Get(string key, bool fallback)
        {
            try { return Preferences.Default.Get(key, fallback); } catch { return fallback; }
        }

        private static void Set(string key, bool value)
        {
            try { Preferences.Default.Set(key, value); }
            catch (Exception ex) { Diag.Warn("settings", $"{key} not saved: {ex.Message}"); }
            Diag.User("settings", $"{key} = {value}");
        }
    }
}
