namespace Horus.Domain.Interfaces
{
    /// <summary>
    /// Keeps the locally known account and subscription in step with the server.
    ///
    /// Access is granted server-side, so the client only ever learns about it by asking.
    /// Before this existed the only triggers were sign-in and navigating <i>onto</i> Home,
    /// which meant a subscription granted while the user sat on the Home screen was
    /// invisible until they restarted or signed out and back in.
    /// </summary>
    public interface IAccountSync
    {
        /// <summary>Raised on the main thread after a refresh that changed the account.</summary>
        event EventHandler? AccountRefreshed;

        /// <summary>
        /// The most recent <c>/whoami</c> that came back, or null if none has. Consumers
        /// that need a field the auth service does not persist — the egress IP — read it
        /// here instead of issuing their own request.
        /// </summary>
        Models.WhoAmIResponse? Last { get; }

        /// <summary>
        /// Refreshes now. Concurrent callers share one in-flight request rather than
        /// stacking up round trips. Never throws — offline is not an error here.
        /// </summary>
        Task RefreshNowAsync(CancellationToken ct = default);

        /// <summary>The app became visible: refresh immediately and resume polling.</summary>
        void OnForeground();

        /// <summary>The app went away: stop polling. Nothing here is worth waking a device for.</summary>
        void OnBackground();
    }
}
