using Horus.Domain.Interfaces;
using System.Net.Sockets;

namespace Horus.Application
{
    /// <summary>
    /// Probes the API server on startup and whenever connectivity changes.
    /// Automatically switches to local mode when the server is unreachable,
    /// and back to API mode when it becomes reachable again.
    ///
    /// In ADMIN_MODE builds the user can also force the mode via SetLocalMode().
    /// </summary>
    public class LocalModeService : ILocalModeService
    {
        private bool _localMode;
        private bool _forcedByUser;

        public bool IsLocalMode => _localMode;

        public event EventHandler<bool>? LocalModeChanged;

        public async Task<bool> ProbeApiAsync(CancellationToken ct = default)
        {
            if (_forcedByUser) return !_localMode; // respect manual override

            var uri = new Uri(AppConfiguration.ApiBaseUrl);
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);

            bool reachable = await TcpProbeAsync(host, port, ct);
            SetMode(!reachable);
            return reachable;
        }

        public void SetLocalMode(bool enabled)
        {
            _forcedByUser = true;
            SetMode(enabled);
        }

        // ── Private ──────────────────────────────────────────────────────────

        private void SetMode(bool localMode)
        {
            if (_localMode == localMode) return;
            _localMode = localMode;
            LocalModeChanged?.Invoke(this, localMode);
        }

        private static async Task<bool> TcpProbeAsync(string host, int port, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
