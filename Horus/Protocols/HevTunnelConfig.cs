using Horus.Domain.Models;

namespace Horus.Protocols
{
    /// <summary>
    /// hev-socks5-tunnel's YAML config, shared by every platform that runs it.
    ///
    /// Android and Windows drive the same binary in two different modes — Android hands it
    /// an already-open TUN fd, Windows lets it create a wintun adapter itself — but the
    /// config is identical, and it has to be: <c>socks5.port</c> here and
    /// <see cref="XrayConfig.DefaultSocksPort"/> are two halves of one contract across a
    /// language boundary. A mismatch establishes a tunnel that carries nothing, and nothing
    /// in either language links the two. Keeping one generator means the contract can only
    /// be broken in one place, and <c>SocksPortContractTests</c> guards that place.
    /// </summary>
    public static class HevTunnelConfig
    {
        /// <summary>
        /// Interface name. Ignored on Android (the fd is passed in already open); on Windows
        /// this becomes the wintun adapter name, which is what <c>netsh</c> and the route
        /// table address it by — so it must stay free of spaces and quoting hazards.
        /// </summary>
        public const string TunnelName = "Horus";

        public const string Ipv4Address = "198.18.0.1";
        public const string Ipv6Address = "fc00::1";

        /// <summary>
        /// Must also be applied to the Windows IP subinterface: wintun reports 65535 by
        /// default, and anything larger than this is dropped by hev's reader.
        /// </summary>
        public const int Mtu = 8500;

        /// <summary>
        /// <paramref name="logFile"/> matters because stderr goes nowhere useful in either
        /// host — /dev/null inside an Android app process, and no console at all when the
        /// Windows build is spawned without a window. Without a real path this half of the
        /// pipeline is undiagnosable.
        ///
        /// <paramref name="socksPort"/> must be the same value the core's inbound was
        /// rendered with — see <see cref="SocksPortAllocator"/>. It is a parameter rather
        /// than a constant precisely so that both ends move together when 1080 is taken.
        /// </summary>
        public static string Build(string logFile, string logLevel, int socksPort) => $"""
misc:
  task-stack-size: 81920
  log-file: {logFile.Replace('\\', '/')}
  log-level: {logLevel}
  log-max-size: {MaxLogBytes}
tunnel:
  name: {TunnelName}
  multi-queue: false
  ipv4: {Ipv4Address}
  ipv6: '{Ipv6Address}'
  mtu: {Mtu}
socks5:
  port: {socksPort}
  address: 127.0.0.1
  udp: 'udp'
""";

        /// <summary>
        /// Cap on the log file, in bytes, enforced inside the bridge.
        ///
        /// <para>Upstream's logger appends forever. A session here can last weeks, and
        /// at the verbose level a user is asked to turn on when something is wrong there
        /// is nothing to stop it filling the device. The bridge truncates in place on
        /// reaching this — see packaging/android/hev-patches for why truncation rather
        /// than rotation. A stock upstream build simply ignores the key.</para>
        ///
        /// <para>2 MiB: far more than a healthy session at the shipping level ever
        /// writes, and small enough to be harmless if verbose logging is left on.</para>
        /// </summary>
        public const int MaxLogBytes = 2 * 1024 * 1024;

        /// <summary>
        /// At <c>warn</c> a healthy-looking but dead tunnel writes an empty log, which is
        /// indistinguishable from "hev never started". Debug builds log every connection so
        /// the TUN half is actually diagnosable; release stays quiet.
        /// </summary>
        public const string DefaultLogLevel =
#if DEBUG
            "info";
#else
            "warn";
#endif
    }
}
