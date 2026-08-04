namespace Horus.Domain.Models
{
    public class SubscriptionInfo
    {
        /// <summary>Session token the state was read with.</summary>
        public string APIKey { get; set; } = string.Empty;

        /// <summary>Subscription expiry; <see cref="DateTime.MinValue"/> when there is none.</summary>
        public DateTime ExpireAt { get; set; }

        public bool IsActive => ExpireAt > DateTime.UtcNow;

        public int DaysLeft => IsActive
            ? Math.Max((int)Math.Ceiling((ExpireAt - DateTime.UtcNow).TotalDays), 0)
            : 0;
    }
}
