namespace Horus.Platforms.Windows.Tunnel
{
    public enum DeadProxyResponse
    {
        /// <summary>Keep the tunnel routed to the proxy: nothing it would carry leaves without the VPN.</summary>
        HoldClosed,

        /// <summary>Keep the tunnel, route its traffic around the dead proxy, keep trying to get it back.</summary>
        FailOpen
    }

    /// <summary>
    /// What the tunnel does once a recovery could not bring the proxy back.
    ///
    /// <para>It never goes down for it. Taking the adapter away used to be how the user got
    /// their connection back after a few failed recoveries, and every connection on the machine
    /// went with it — the game routed direct included, whose packets the TUN was carrying
    /// perfectly well. Failing open is a routing reload: open connections keep going, and the
    /// rest leave directly until the proxy answers again, exactly as long as dropping the
    /// tunnel used to take.</para>
    ///
    /// <para>Pure — <c>Horus.Tests</c> pins it.</para>
    /// </summary>
    public static class DeadProxyPolicy
    {
        /// <summary>Failed recoveries the tunnel stays closed for: a dead link usually comes back.</summary>
        public const int ClosedRecoveries = 4;

        public static DeadProxyResponse After(int failedRecoveries, bool killSwitch) =>
            killSwitch || failedRecoveries < ClosedRecoveries ? DeadProxyResponse.HoldClosed : DeadProxyResponse.FailOpen;
    }
}
