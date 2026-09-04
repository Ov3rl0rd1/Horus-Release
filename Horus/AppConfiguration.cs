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
        /// Where <c>geoip.dat</c> / <c>geosite.dat</c> and their <c>.sha256sum</c> sidecars
        /// are fetched from. Empty disables geo routing entirely.
        ///
        /// <para>Default is the Russia-specific rule set, which is the one that matters for
        /// these users: it carries <c>geosite:ru-blocked</c> (what must be proxied) and
        /// <c>geosite:ru-available-only-inside</c> (what must not be), alongside every
        /// v2fly category. The generic v2fly build has neither.</para>
        ///
        /// <para>The files are large — about 18 MB and 74 MB — which is why they are
        /// downloaded on an unmetered network rather than shipped in the APK.</para>
        /// </summary>
        public static string GeoAssetsBaseUrl { get; set; } =
            "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download";

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
