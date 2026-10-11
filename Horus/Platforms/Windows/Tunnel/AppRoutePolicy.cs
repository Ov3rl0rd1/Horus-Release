using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Tunnel
{
    public enum AppRoute { Vpn, Direct }

    /// <summary>
    /// What the split-tunnel rules mean for one application, and whether its open
    /// connections still follow an older rule.
    ///
    /// <para>Pure — <c>Horus.Tests</c> pins it. Site and geo rules are not considered: they
    /// pick a route per destination, so an application on the VPN may legitimately have some
    /// connections direct. Only the split choice is the user's statement about the app.</para>
    /// </summary>
    public static class AppRoutePolicy
    {
        /// <summary>Where new connections of the application go under the split mode.</summary>
        public static AppRoute For(SplitTunnelingMode mode, bool chosen) => mode switch
        {
            SplitTunnelingMode.Blacklist => chosen ? AppRoute.Direct : AppRoute.Vpn,
            SplitTunnelingMode.Whitelist => chosen ? AppRoute.Vpn : AppRoute.Direct,
            _ => AppRoute.Vpn
        };

        /// <summary>
        /// The application has connections older than the latest change to its rule. Open
        /// connections keep the route they started with, so these still run the old way until
        /// the application reconnects — which "restart" makes it do.
        /// </summary>
        public static bool NeedsRestart(AppTraffic? live, long ruleChangedAtMs) =>
            live is { Total: > 0, OldestStartedMs: > 0 } && ruleChangedAtMs > 0 && live.OldestStartedMs < ruleChangedAtMs;

        /// <summary>The live route, in the words the list shows.</summary>
        public static string Describe(RouteMix mix) => mix switch
        {
            RouteMix.Vpn => "Через VPN",
            RouteMix.Direct => "Напрямую",
            RouteMix.Mixed => "VPN и напрямую",
            _ => "Служебный трафик"
        };

        /// <summary>The toggle's caption for a mode: what switching it on does.</summary>
        public static string ToggleCaption(SplitTunnelingMode mode) => mode switch
        {
            SplitTunnelingMode.Blacklist => "Мимо VPN",
            SplitTunnelingMode.Whitelist => "Через VPN",
            _ => string.Empty
        };
    }
}
