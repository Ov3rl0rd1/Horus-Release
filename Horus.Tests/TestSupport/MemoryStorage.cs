using Horus.Domain.Interfaces;

namespace Horus.Tests.TestSupport;

/// <summary><see cref="IStorageService"/> in memory — what SecureStorage holds on a device.</summary>
public sealed class MemoryStorage : IStorageService
{
    private string? _session;
    private DateTime? _sessionExpiresAt, _subscription;
    private string? _username, _email;

    public Task Initialization => Task.CompletedTask;
    public string? Session() => _session;
    public DateTime? SessionExpiresAt() => _sessionExpiresAt;
    public string? Username() => _username;
    public string? Email() => _email;
    public DateTime? Subscription() => _subscription;

    public Task UpdateSessionAsync(string session, DateTime? sessionExpiresAt = null)
    {
        _session = session;
        _sessionExpiresAt = sessionExpiresAt;
        return Task.CompletedTask;
    }

    public Task UpdateAccountAsync(string username, string? email, DateTime? subscription)
    {
        (_username, _email, _subscription) = (username, email, subscription);
        return Task.CompletedTask;
    }

    public Task UpdateSubscriptionAsync(DateTime? subscription)
    {
        _subscription = subscription;
        return Task.CompletedTask;
    }

    public void Clear() => _session = _username = _email = null;
}
