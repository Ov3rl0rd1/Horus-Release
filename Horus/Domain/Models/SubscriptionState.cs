namespace Horus.Domain.Models
{
    /// <summary>
    /// What the app currently knows about the subscription.
    ///
    /// <see cref="Unknown"/> exists so a restored session can be treated as good until the
    /// server contradicts it. Collapsing "not asked yet" into "expired" is what made the
    /// app flash a renew banner — and gate connecting — on every cold start, before
    /// <c>/whoami</c> had answered.
    /// </summary>
    public enum SubscriptionState
    {
        /// <summary>Not verified this run. Assume valid; show nothing.</summary>
        Unknown,

        /// <summary>The server confirmed an expiry in the future.</summary>
        Active,

        /// <summary>The server confirmed there is no active subscription.</summary>
        Expired
    }
}
