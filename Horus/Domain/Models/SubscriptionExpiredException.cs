namespace Horus.Domain.Models
{
    /// <summary>
    /// The API refused to hand out a server config because the subscription has run out
    /// (403 from <c>GET /servers/connect</c>).
    ///
    /// Typed rather than a bare message so the UI can route to the payment sheet instead
    /// of showing a dead-end error dialog — the client-side gate is optimistic by design,
    /// which makes this the authoritative signal.
    /// </summary>
    public class SubscriptionExpiredException : Exception
    {
        public SubscriptionExpiredException(string message) : base(message) { }
    }
}
