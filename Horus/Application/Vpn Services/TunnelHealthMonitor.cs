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
        /// Bytes sent before the cheap tier will consider escalating. Roughly a few
        /// retransmitted handshakes: enough that ordinary keepalives and a single lost
        /// packet do not trigger a probe.
        /// </summary>
        private const long UnansweredBytesThreshold = 8 * 1024;

        /// <summary>
        /// How much smaller the downlink has to be than the uplink to count as "nothing
        /// came back".
        ///
        /// The first version of this test asked for <c>received == 0</c>, and that was
        /// wrong in a way only a real device showed. A tunnel whose outbound is dead does
        /// not go silent: the far side of each stalled session eventually produces RSTs and
        /// ICMP errors, and those flow back through the bridge as real bytes. Measured on
        /// the failure this was written for — 25 399 bytes out in 126 packets against 3 520
        /// bytes back in 81 packets, about 43 bytes per packet, which is resets and nothing
        /// else. Any single byte reset the suspicion counter, so the monitor reported a
        /// perfectly healthy tunnel for twenty minutes while 190 sessions piled up.
        ///
        /// Real traffic never looks like this: a downlink an eighth of the uplink does not
        /// happen for a working connection outside of a pure upload, which is why the
        /// packet-size check below rules that case out too.
        /// </summary>
        private const long StarvedDownlinkRatio = 8;

        /// <summary>
        /// Mean received-packet size below which the downlink is control traffic rather
        /// than data. Resets and ICMP errors are tiny; even a bare TCP ACK stream carries
        /// more once the link is really working.
        /// </summary>
        private const long ControlPacketSize = 96;

        /// <summary>Consecutive suspicious samples required before probing.</summary>
        private const int SuspicionLimit = 2;

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

        private readonly IVpnPlatformService _platform;
        private readonly IApiService _api;
        private readonly IDeviceConditions _device;
        private readonly INetworkMonitor _network;

        private CancellationTokenSource? _cts;
        private Endpoint _endpoint;
        private long _lastTx, _lastRx, _lastRxPackets;
        private bool _hasBaseline;
        private int _suspicion;

        /// <summary>The last measured counter delta, carried into the log line.</summary>
        private string _lastSample = "no sample";

        /// <summary>Raised when the tunnel is not healthy. Never raised for <see cref="TunnelHealth.Healthy"/>.</summary>
        public event EventHandler<TunnelHealthEventArgs>? Unhealthy;

        public TunnelHealthMonitor(
            IVpnPlatformService platform, IApiService api, IDeviceConditions device, INetworkMonitor network)
        {
            _platform = platform;
            _api = api;
            _device = device;
            _network = network;
        }

        /// <summary>
        /// What the monitor needs to test the tunnel. Only the SOCKS port: the node's
        /// address used to be carried here for a TCP reachability probe, which was removed
        /// because it could not be sound for a UDP transport.
        /// </summary>
        public readonly record struct Endpoint(int SocksPort);

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
                    await WaitAsync(CurrentInterval(), ct).ConfigureAwait(false);

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

                    // Report what was measured, not the constant it was compared against —
                    // the first version logged the threshold and told us nothing.
                    Raise(probed, $"{_lastSample} -> {probed}");
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

        /// <summary>
        /// Sleeps until the interval elapses or someone calls <see cref="WakeNow"/>.
        ///
        /// A plain delay was why waking the phone did not help: the loop was already
        /// several seconds into a 90-second sleep chosen while the screen was off, and
        /// nothing could shorten it. The first check after the user picked the phone up
        /// could be a minute and a half late, and two samples are needed before a probe —
        /// which is the "rebuilt two or three minutes after I turned the screen on" the
        /// device actually showed.
        /// </summary>
        private async Task WaitAsync(TimeSpan interval, CancellationToken ct)
        {
            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _wake, wake);

            using var registration = ct.Register(() => wake.TrySetResult());
            await Task.WhenAny(wake.Task, Task.Delay(interval, ct)).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
        }

        private TaskCompletionSource? _wake;

        /// <summary>
        /// Cuts the current wait short so the next check happens immediately. Called when
        /// the device wakes: that is the moment a stale tunnel starts costing the user
        /// something, and the moment it is worth spending a probe on.
        /// </summary>
        public void WakeNow()
        {
            Interlocked.Exchange(ref _wake, null)?.TrySetResult();
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

            // Short array means the bridge is not running — the packets have nowhere to go.
            // This is deliberately not read as "idle": the two used to be the same value
            // and a dead bridge therefore looked exactly like a sleeping phone.
            if (counters.Length < 4) return TunnelHealth.TunnelDead;

            var tx = counters[1];
            var rx = counters[3];
            var rxPackets = counters[2];

            if (!_hasBaseline || tx < _lastTx || rx < _lastRx || rxPackets < _lastRxPackets)
            {
                _lastTx = tx; _lastRx = rx; _lastRxPackets = rxPackets;
                _hasBaseline = true; _suspicion = 0;
                return TunnelHealth.Healthy;
            }

            var sent = tx - _lastTx;
            var received = rx - _lastRx;
            var receivedPackets = rxPackets - _lastRxPackets;
            _lastTx = tx; _lastRx = rx; _lastRxPackets = rxPackets;

            // Too little went out to conclude anything. Not evidence of health, but not
            // evidence of failure either, and inventing a failure from an idle device is
            // how a client ends up reconnecting all night.
            if (sent < UnansweredBytesThreshold) return TunnelHealth.Healthy;

            var starved = received * StarvedDownlinkRatio < sent;

            // A genuine large upload also starves the downlink, but its ACKs are ordinary
            // packets. A dead outbound answers in resets, which are tiny — so the mean
            // packet size separates the two.
            var controlOnly = receivedPackets == 0 || received / Math.Max(receivedPackets, 1) < ControlPacketSize;

            _lastSample = $"out {sent}B, back {received}B in {receivedPackets}p";

            if (!starved || !controlOnly) { _suspicion = 0; return TunnelHealth.Healthy; }

            // Sending into what is effectively silence. One sample could be a slow request;
            // two in a row is a pattern worth paying for a probe to explain.
            if (++_suspicion < SuspicionLimit)
            {
                // On the first suspicion, hand the question to the platform: it will
                // re-run its own validation through the tunnel and answer with a
                // capability change, which usually lands before our second sample does.
                _network.ReportTunnelSuspect();
                return TunnelHealth.Healthy;
            }

            return TunnelHealth.OutboundDead;
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
            //
            // Ask the platform, do not probe. The previous version opened a TCP connection
            // to the node and read a failure as "no internet", which is unsound: Hysteria2
            // is QUIC over UDP, so a node serving only UDP on that port refuses every TCP
            // connect. With Hysteria2 first in the fallback order that made the answer
            // permanently "no internet" — the tunnel was then held, never rebuilt, and the
            // device sat with a dead VPN indefinitely. Observed exactly that on a device
            // whose Wi-Fi was up the whole time.
            //
            // NET_CAPABILITY_VALIDATED on a non-VPN network is the system's own verdict on
            // whether traffic reaches the internet. It costs no packets, no wakeup, and it
            // cannot be fooled by the node's choice of transport.
            var link = ReadLink();
            return link ? TunnelHealth.OutboundDead : TunnelHealth.NoInternet;
        }

        private bool ReadLink()
        {
            try { return _device.Read().HasNetwork; }
            catch { return true; } // unreadable: assume the link is fine and blame the tunnel
        }

        private void Raise(TunnelHealth health, string detail)
        {
            // Logged here as well as by the manager: if the recovery that follows goes
            // wrong, this line is the last thing that says what the monitor actually saw.
            Diag.Write($"[health] {health}: {detail}");

            Stop();
            Unhealthy?.Invoke(this, new TunnelHealthEventArgs(health, detail));
        }
    }
}
