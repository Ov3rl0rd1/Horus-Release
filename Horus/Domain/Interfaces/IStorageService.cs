namespace Horus.Domain.Interfaces
{
    public interface IStorageService
    {
        Task Initialization { get; }

        string? Token();
        string? Session();
        string? Username();
        DateTime? Subscription();

        Task UpdateAsync(string token, string session, string username, DateTime? subscription);
        Task UpdateTokenAsync(string token);
    }
}
