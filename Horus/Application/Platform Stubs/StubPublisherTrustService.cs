using Horus.Domain.Interfaces;

namespace Horus.Application
{
    /// <summary>
    /// Publisher trust is a Windows concern. Android packages carry their signature inside
    /// the APK and the platform checks it at install time, so there is nothing for the app
    /// to offer.
    /// </summary>
    public sealed class StubPublisherTrustService : IPublisherTrustService
    {
        public bool NeedsTrust => false;
        public string? Thumbprint => null;
        public Task<bool> InstallAsync() => Task.FromResult(false);
    }
}
