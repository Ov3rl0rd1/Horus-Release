using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// One funnel for every "check or rebuild the tunnel" request, with a debounce.
    ///
    /// <para><b>The problem it solves is a storm, not a single event.</b> A lift, a train
    /// tunnel, or the edge of Wi-Fi coverage produces five to ten network transitions in
    /// twenty seconds. Each one used to fire its own probe — a six-second HTTP request
    /// through SOCKS5 — so ten transitions meant ten concurrent probes, ten wakeups, and a
    /// non-deterministic race over which one got to declare the tunnel dead. The user
    /// experiences that as the VPN reconnecting repeatedly while they walk to the platform.</para>
    ///
    /// <para><b>Two suppressions, not one.</b> The debounce collapses a burst into a single
    /// action after things settle. On top of that, a request whose reason matches the one
    /// already queued is dropped outright, so a signal that repeats faster than the debounce
    /// window cannot keep pushing the action further away — which would be the failure mode
    /// of a debounce on its own.</para>
    ///
    /// <para>Both borrowed from Rethink, which routes every restart through a single
    /// <c>MutableStateFlow.debounce(3s)</c> and encodes coarse time into the reason string
    /// so identical values are dropped by StateFlow's own equality. The reason travels with
    /// the request and ends up in the log, so "why did it reconnect" is answerable after
    /// the fact.</para>
    /// </summary>
    public sealed class RecoveryTrigger : IDisposable
    {
        /// <summary>
        /// Long enough for a handover to settle, short enough that the user does not sit
        /// looking at a dead tunnel. Same value Rethink settled on.
        /// </summary>
        public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

        private readonly Func<string, Task> _action;
        private readonly object _sync = new();

        private CancellationTokenSource? _cts;
        private string? _queuedReason;
        private bool _disposed;

        public RecoveryTrigger(Func<string, Task> action) => _action = action;

        /// <summary>The reason currently waiting to fire, if any. Diagnostics only.</summary>
        public string? Pending { get { lock (_sync) return _queuedReason; } }

        /// <summary>
        /// Asks for the action to run once things have been quiet for <see cref="Debounce"/>.
        /// Cheap and safe to call from a network callback.
        /// </summary>
        public void Request(string reason)
        {
            lock (_sync)
            {
                if (_disposed) return;

                // Already queued under the same reason: leave the existing timer alone. If
                // this reset it, a signal repeating faster than the window would postpone
                // the action forever — a debounce that never fires is worse than none.
                if (_queuedReason == reason && _cts is { IsCancellationRequested: false })
                {
                    Diag.Trace("recover", $"trigger coalesced: {reason}");
                    return;
                }

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _queuedReason = reason;

                var token = _cts.Token;
                Diag.Trace("recover", $"trigger queued in {Debounce.TotalSeconds:F0}s: {reason}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Debounce, token).ConfigureAwait(false);

                        lock (_sync)
                        {
                            if (token.IsCancellationRequested) return;
                            _queuedReason = null;
                        }

                        await _action(reason).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* superseded */ }
                    catch (Exception ex)
                    {
                        Diag.Warn("recover", $"trigger action failed: {ex.Message}");
                    }
                }, token);
            }
        }

        /// <summary>Drops anything queued. Used on disconnect, so a pending action cannot
        /// fire after the user has told the app to stop.</summary>
        public void Cancel()
        {
            lock (_sync)
            {
                _cts?.Cancel();
                _queuedReason = null;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _queuedReason = null;
            }
        }
    }
}
