namespace Horus.Domain.Interfaces
{
    public interface IStorageService
    {
        Task Initialization { get; }

        string? Session();
        /// <summary>When the stored session token stops being accepted by the API.</summary>
        DateTime? SessionExpiresAt();
        string? Username();
        string? Email();
        /// <summary>Subscription expiry, as last reported by <c>/whoami</c>.</summary>
        DateTime? Subscription();

        Task UpdateSessionAsync(string session, DateTime? sessionExpiresAt = null);
        Task UpdateAccountAsync(string username, string? email, DateTime? subscription);
        Task UpdateSubscriptionAsync(DateTime? subscription);

        void Clear();
    }
}
