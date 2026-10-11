namespace Horus.Application
{
    /// <summary>
    /// When a connect that succeeded on endpoints stored on the device should ask the API for
    /// fresh ones, in the background, for next time.
    ///
    /// <para>The cache is dropped only when every stored endpoint fails, so a node change that
    /// breaks one protocol while another still works never reached the device: the app kept
    /// running the stale profile for up to a day (seen in a user's log: Hysteria2 from a
    /// 23-hour-old cache, failing, while VLESS carried the session). Refreshing after such a
    /// connect makes a fix on the node — an ALPN, a port, hopping turned off — arrive with
    /// the next connect instead.</para>
    /// </summary>
    public static class CacheRefreshPolicy
    {
        /// <summary>Stored endpoints older than this are refreshed after any connect.</summary>
        public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(1);

        /// <param name="fromCache">The connect used stored endpoints.</param>
        /// <param name="someEndpointFailed">An endpoint tried before the one that worked failed.</param>
        /// <param name="age">How old the stored set is; null when unknown.</param>
        public static bool AfterConnect(bool fromCache, bool someEndpointFailed, TimeSpan? age) =>
            fromCache && (someEndpointFailed || age is null || age >= RefreshAfter);
    }
}
