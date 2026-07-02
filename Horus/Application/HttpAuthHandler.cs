using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    internal class HttpAuthHandler : DelegatingHandler
    {
        private readonly IStorageService _storage;

        public HttpAuthHandler(IStorageService storage, bool verifyServerCertificate=true)
        {
            _storage = storage;

            if (verifyServerCertificate == false)
            {
                InnerHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
            }
            else
                InnerHandler = new HttpClientHandler();
        }

        public bool HasSession() => string.IsNullOrWhiteSpace(_storage.Session()) == false;

        public async Task UpdateSession(string session) => await _storage.UpdateSessionAsync(session);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? currentSession = _storage.Session();

            if (!string.IsNullOrWhiteSpace(currentSession))
                request.Headers.Add(ApiConsts.SESSION_HEADER, currentSession);

            return base.SendAsync(request, cancellationToken);
        }
    }
}
