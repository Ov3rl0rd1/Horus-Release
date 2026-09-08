using System.Reflection;

namespace Horus
{
    public static class AppConfiguration
    {
        public static string ApiBaseUrl { get; set; } = "http://localhost";

        public static string AppVersion { get; } =
            typeof(AppConfiguration).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? "1.0.0";

        public static string SupportEmail { get; set; } = "support@horus-vpn.app";

        /// <summary>Telegram handle shown on the Settings screen for support contact.</summary>
        public static string SupportHandle { get; set; } = "@horus_vpn";

        /// <summary>
        /// The GitHub releases page, exactly as a person would paste it from a browser.
        /// The updater derives the API endpoint from it, so pointing this at a different
        /// repository is all that is needed to move the release feed.
        ///
        /// Pre-releases are included; drafts are not. When GitHub is unreachable — which
        /// for these users is the normal state with the tunnel down — the updater falls
        /// back to <see cref="ApiBaseUrl"/> and reads the version out of
        /// <c>SHA256SUMS.txt</c>. Set this to an empty string to disable the GitHub source.
        /// </summary>
        public static string UpdateReleasesUrl { get; set; } =
            "https://github.com/Ov3rl0rd1/Horus-Release/releases";

        /// <summary>
        /// Where <c>geoip.dat</c> is fetched from. The <c>.sha256sum</c> sidecar is assumed
        /// to sit beside it under the same name; nothing installs without one.
        ///
        /// <para><b>Two URLs rather than one base, because the files come from two
        /// repositories.</b> <c>horus-geoip</c> and <c>horus-geosite</c> are separate forks
        /// with separate release cadences — geoip monthly from MaxMind, geosite daily from
        /// v2fly's domain list — and each publishes a <c>release</c> branch holding exactly
        /// its <c>.dat</c> and that file's checksum. Pointing at the branch rather than at a
        /// GitHub release keeps the URL constant: the branch is force-pushed on every build,
        /// so there is no tag to track and no API call to make.</para>
        ///
        /// <para>Both are Russia-only builds, which is what makes the pair small enough
        /// (~400 KB together) to refresh casually. Empty disables geo routing entirely —
        /// and it must be empty on both or neither, since the generated config names a
        /// category from each file.</para>
        /// </summary>
        public static string GeoIpUrl { get; set; } =
            "https://raw.githubusercontent.com/Ov3rl0rd1/horus-geoip/release/geoip.dat";

        /// <summary>Where <c>geosite.dat</c> is fetched from. See <see cref="GeoIpUrl"/>.</summary>
        public static string GeoSiteUrl { get; set; } =
            "https://raw.githubusercontent.com/Ov3rl0rd1/horus-geosite/release/geosite.dat";

        /// <summary>
        /// Android package names that must always bypass the tunnel, whatever split-tunnel
        /// mode the user picks. Populated from <c>BlockedPackages</c> in appsettings.json.
        ///
        /// Intended for apps that are known to break behind a VPN — banking apps that
        /// refuse to run, carrier services tied to the local network, anything that would
        /// otherwise generate support tickets. The user can see the list but not edit it.
        /// </summary>
        public static IReadOnlyList<string> BlockedPackages { get; set; } = [];

#if DEBUG
        /// <summary>
        /// Accept any TLS certificate. Debug-only and opt-in: the app talks to the
        /// production API in every configuration, and a VPN client that silently skips
        /// certificate validation is a man-in-the-middle waiting to happen. Turn it on
        /// deliberately for a proxy-inspection session, then turn it back off.
        /// </summary>
        public static bool AllowInsecureTls { get; set; }

        /// <summary>
        /// DEBUG only: when true, the auth screens and the connect button short-circuit to
        /// local fakes so the UI can be walked without a backend. Off by default so debug
        /// builds exercise the real API.
        /// </summary>
        public static bool UseDevBypass { get; set; }
#else
        /// <summary>Always false in Release — certificate validation is never optional in a shipping build.</summary>
        public static bool AllowInsecureTls => false;
#endif
    }
}
