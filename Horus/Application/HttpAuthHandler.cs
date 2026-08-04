using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using System.Net;

namespace Horus.Application
{
    internal class HttpAuthHandler : DelegatingHandler
    {
        private readonly IStorageService _storage;

        /// <summary>
        /// Raised when the API rejects the stored session on an authenticated route.
        /// Without this, a revoked session is invisible: most calls turn a 401 into a
        /// null result, so the UI just shows empty lists and stale data forever.
        /// </summary>
        public event EventHandler? Unauthorized;

        /// <summary>Debounce so one burst of parallel calls raises a single sign-out.</summary>
        private DateTime _lastUnauthorizedAt = DateTime.MinValue;
        private static readonly TimeSpan UnauthorizedDebounce = TimeSpan.FromSeconds(10);

        public HttpAuthHandler(IStorageService storage, bool verifyServerCertificate = true)
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? currentSession = _storage.Session();

            if (!string.IsNullOrWhiteSpace(currentSession))
                request.Headers.Add(ApiConsts.SESSION_HEADER, currentSession);

            var response = await base.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized && ShouldSignOutOn(request))
                RaiseUnauthorized();

            return response;
        }

        /// <summary>
        /// Only authenticated routes imply a dead session. A 401 from /auth/* means the
        /// credentials in that request were wrong, which the calling screen handles itself.
        /// </summary>
        private static bool ShouldSignOutOn(HttpRequestMessage request)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return !path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase);
        }

        private void RaiseUnauthorized()
        {
            var now = DateTime.UtcNow;
            if (now - _lastUnauthorizedAt < UnauthorizedDebounce) return;
            _lastUnauthorizedAt = now;

            Unauthorized?.Invoke(this, EventArgs.Empty);
        }
    }
}
