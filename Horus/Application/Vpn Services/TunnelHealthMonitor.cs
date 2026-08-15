using System.Net.Sockets;
using Horus.Domain.Events;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;
using Horus.Protocols;

namespace Horus.Application
{
    /// <summary>
    /// Decides whether the tunnel is actually working, and when it is not, <i>which part</i>
    /// failed.
    ///
    /// <para>The old watchdog asked one question every five seconds — "is xray still
    /// running?" — which misses every failure that matters. The core stays up when the node
    /// stops answering; it stays up when Doze cuts the app's network; it stays up when a
    /// Wi-Fi to mobile handover kills every established connection. In all three the user
    /// sees ЗАЩИЩЕНО over a tunnel carrying nothing.</para>
    ///
    /// <para><b>Two tiers, so the cheap one can run often and the expensive one almost
    /// never.</b> The cheap tier reads counters already maintained by the bridge and costs
    /// no network and no wakeup. It only escalates on a specific, meaningful pattern: bytes
    /// going out with nothing coming back. Traffic in both directions is proof of health;
    /// traffic in neither direction means the device is idle and says nothing at all — and
    /// treating that silence as failure is exactly how a client ends up reconnecting all
    /// night on a phone nobody is touching.</para>
    ///
    /// <para><b>The expensive tier answers the question the user actually asked</b> — is it
    /// the internet, a component the system killed, or the protocol? Two probes:</para>
    /// <list type="number">
    /// <item>Fetch the egress IP through the core's own SOCKS5 inbound. Success means the
    /// whole chain works and the silence was innocent.</item>
    /// <item>Otherwise open a bare TCP connection to the node. That path bypasses the
    /// tunnel on both platforms — by UID exclusion on Android, by the <c>/32</c> host route
    /// on Windows — so it tests the physical link without the tunnel in the way. Reachable
    /// means the link is fine and this protocol is not working here, which is a reason to
    /// try the next one. Unreachable means the device has no usable connectivity, and the
    /// only correct response is to do nothing and wait.</item>
    /// </list>
    ///
    /// <para>Cadence follows the screen. With the screen on, a stall is something the user
    /// is staring at, so checks run every 15 seconds. With the screen off nobody is waiting
    /// on a page to load, and the check drops to every 90 seconds — still far faster than
    /// the user noticing in the morning, and cheap enough to leave running all night.</para>
    /// </summary>
    public sealed class TunnelHealthMonitor
    {
        /// <summary>Screen on: the user is watching, so notice quickly.</summary>
        private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(15);

        /// <summary>Screen off: nothing is waiting, so sample rarely and stay cheap.</summary>
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Bytes sent with nothing received before the cheap tier escalates. Roughly a few
        /// retransmitted handshakes: enough that ordinary keepalives and a single lost
        /// packet do not trigger a probe.
        /// </summary>
        private const long UnansweredBytesThreshold = 8 * 1024;

        /// <summary>Consecutive suspicious samples required before probing.</summary>
        private const int SuspicionLimit = 2;

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

        private readonly IVpnPlatformService _platform;
        private readonly IApiService _api;
        private readonly IDeviceConditions _device;

        private CancellationTokenSource? _cts;
        private Endpoint _endpoint;
        private long _lastTx, _lastRx;
        private bool _hasBaseline;
        private int _suspicion;

        /// <summary>Raised when the tunnel is not healthy. Never raised for <see cref="TunnelHealth.Healthy"/>.</summary>
        public event EventHandler<TunnelHealthEventArgs>? Unhealthy;

        public TunnelHealthMonitor(IVpnPlatformService platform, IApiService api, IDeviceConditions device)
        {
            _platform = platform;
            _api = api;
            _device = device;
        }

        public readonly record struct Endpoint(int SocksPort, string? NodeHost, int NodePort);

        public void Start(Endpoint endpoint)
        {
            Stop();
            _endpoint = endpoint;
            _hasBaseline = false;
            _suspicion = 0;
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>
        /// Forces the expensive check immediately. Called on a network handover, where the
        /// counters cannot help: every established connection through the old link is
        /// already dead and nothing will report it until something tries to use one.
        /// </summary>
        public void ProbeNow(string reason)
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            if (ct.IsCancellationRequested) return;

            _ = Task.Run(async () =>
            {
                var health = await ProbeAsync(ct).ConfigureAwait(false);
                if (health != TunnelHealth.Healthy) Raise(health, reason);
                else _suspicion = 0;
            }, ct);
        }

        // ── Loop ────────────────────────────────────────────────────────────

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(CurrentInterval(), ct).ConfigureAwait(false);

                    var cheap = CheckCounters();
                    if (cheap == TunnelHealth.Healthy) continue;

                    // The two structural failures are certain; there is nothing to probe.
                    if (cheap is TunnelHealth.CoreDead or TunnelHealth.TunnelDead)
                    {
                        Raise(cheap, "detected by the local check");
                        return;
                    }

                    var probed = await ProbeAsync(ct).ConfigureAwait(false);
                    if (probed == TunnelHealth.Healthy) { _suspicion = 0; continue; }

                    Raise(probed, $"{UnansweredBytesThreshold} bytes unanswered, probe says {probed}");
                    return;
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Horus/health] monitor died: {ex}");
            }
        }

        private TimeSpan CurrentInterval()
        {
            try { return _device.Read().IsInteractive ? ActiveInterval : IdleInterval; }
            catch { return ActiveInterval; }
        }

        // ── Cheap tier ──────────────────────────────────────────────────────

        /// <summary>
        /// Reads the bridge's counters. Returns <see cref="TunnelHealth.Healthy"/> for both
        /// "traffic is flowing" and "nothing is happening" — the second is not evidence of
        /// anything, and inventing a failure from it is what would keep a sleeping phone
        /// reconnecting.
        /// </summary>
        private TunnelHealth CheckCounters()
        {
            if (!XrayProtocol.IsCoreRunning) return TunnelHealth.CoreDead;
            if (_platform.CurrentState is TunnelState.Error or TunnelState.Stopped)
                return TunnelHealth.TunnelDead;

            long[] counters;
            try { counters = _platform.GetTunnelStats(); }
            catch { return TunnelHealth.Healthy; }
            if (counters.Length < 4) return TunnelHealth.Healthy;

            var tx = counters[1];
            var rx = counters[3];

            if (!_hasBaseline || tx < _lastTx || rx < _lastRx)
            {
                _lastTx = tx; _lastRx = rx; _hasBaseline = true; _suspicion = 0;
                return TunnelHealth.Healthy;
            }

            var sent = tx - _lastTx;
            var received = rx - _lastRx;
            _lastTx = tx; _lastRx = rx;

            if (received > 0) { _suspicion = 0; return TunnelHealth.Healthy; }
            if (sent < UnansweredBytesThreshold) return TunnelHealth.Healthy;

            // Sending into silence. One sample could be a slow request; two in a row is a
            // pattern worth paying for a probe to explain.
            return ++_suspicion >= SuspicionLimit ? TunnelHealth.OutboundDead : TunnelHealth.Healthy;
        }

        // ── Expensive tier ──────────────────────────────────────────────────

        private async Task<TunnelHealth> ProbeAsync(CancellationToken ct)
        {
            if (!XrayProtocol.IsCoreRunning) return TunnelHealth.CoreDead;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);

                var through = await _api.GetEgressIpAsync(
                    $"socks5://127.0.0.1:{_endpoint.SocksPort}", timeout.Token).ConfigureAwait(false);

                if (through is not null) return TunnelHealth.Healthy;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* no answer through the proxy: fall through to the link test */ }

            // Nothing came back through the tunnel. Is the link itself alive?
            if (_endpoint.NodeHost is null) return TunnelHealth.NoInternet;

            var reachable = await CanReachAsync(_endpoint.NodeHost, _endpoint.NodePort, ct).ConfigureAwait(false);
            return reachable ? TunnelHealth.OutboundDead : TunnelHealth.NoInternet;
        }

        /// <summary>
        /// A bare TCP connect, with no HTTP and no DNS. Both are avoided on purpose: DNS is
        /// carried by the tunnel and would fail for reasons unrelated to the link, and an
        /// HTTP round trip costs far more than the one bit of information wanted here.
        /// </summary>
        private static async Task<bool> CanReachAsync(string host, int port, CancellationToken ct)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);

                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
        }

        private void Raise(TunnelHealth health, string detail)
        {
            Stop();
            Unhealthy?.Invoke(this, new TunnelHealthEventArgs(health, detail));
        }
    }
}
