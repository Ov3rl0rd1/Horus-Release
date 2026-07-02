namespace Horus.Domain.Interfaces
{
    public interface IStorageService
    {
        Task Initialization { get; }

        string? Session();
        string? Username();
        DateTime? Subscription();

        Task UpdateAsync(string session, string username, DateTime? subscription);
        Task UpdateSessionAsync(string session);
    }
}
