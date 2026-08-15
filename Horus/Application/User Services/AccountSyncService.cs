using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// The single place the app asks the server "what is my account?".
    ///
    /// Two problems this replaces:
    ///
    /// <para><b>It never asked again.</b> A refresh happened at sign-in and when the user
    /// navigated <i>onto</i> Home. Someone already sitting on Home while a subscription was
    /// granted server-side saw nothing change until they restarted the app.</para>
    ///
    /// <para><b>It asked three times when it did ask.</b> The old Home refresh called
    /// <c>/whoami</c> directly for the egress IP, then <c>CheckSubscriptionAsync</c> called
    /// it again, then <c>IAuthService.RefreshAccountAsync</c> called it a third time — three
    /// round trips on every tab switch for one snapshot. One call now fans out to all
    /// three consumers.</para>
    ///
    /// The poll cadence is deliberately state-dependent rather than fixed. Waiting for a
    /// grant is a minutes-long situation the user is actively watching, so polling fast is
    /// worth it; a subscription with weeks left is not, and on a phone a fixed fast poll is
    /// pure battery cost. Polling stops entirely in the background — nothing here justifies
    /// waking a sleeping device, and the tunnel does not depend on it.
    /// </summary>
    public sealed class AccountSyncService : IAccountSync, IDisposable
    {
        /// <summary>Subscription comfortably active: this is just drift correction.</summary>
        private static readonly TimeSpan SettledInterval = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Expired, unknown, or nearly out. This is the "I just paid, where is it?" case —
        /// the user is looking at the screen, so the answer should arrive while they watch.
        /// </summary>
        private static readonly TimeSpan WaitingInterval = TimeSpan.FromSeconds(20);

        /// <summary>Below this, treat the subscription as needing close attention.</summary>
        private static readonly TimeSpan ExpiringSoon = TimeSpan.FromDays(2);

        private readonly IAuthService _auth;
        private readonly ISubscriptionService _subscription;

        private readonly object _sync = new();
        private CancellationTokenSource? _pollCts;
        private Task<WhoAmIResponse?>? _inFlight;
        private bool _foreground;

        public event EventHandler? AccountRefreshed;

        public WhoAmIResponse? Last { get; private set; }

        public AccountSyncService(IAuthService auth, ISubscriptionService subscription)
        {
            _auth = auth;
            _subscription = subscription;
        }

        public async Task RefreshNowAsync(CancellationToken ct = default)
        {
            // Coalesce: navigation, resume and the poll tick routinely land together, and
            // three identical requests would be three chances to rate-limit ourselves.
            Task<WhoAmIResponse?> task;
            lock (_sync)
            {
                _inFlight ??= RunRefreshAsync();
                task = _inFlight;
            }

            try { await task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* offline: keep whatever we last knew */ }
        }

        private async Task<WhoAmIResponse?> RunRefreshAsync()
        {
            try
            {
                // One /whoami. IAuthService owns the call because it is what persists the
                // account and derives SubscriptionState; everything else consumes its result.
                var me = await _auth.RefreshAccountAsync().ConfigureAwait(false);
                await _subscription.ApplyAsync(me).ConfigureAwait(false);

                if (me is not null) Last = me;

                if (me is not null)
                    MainThread.BeginInvokeOnMainThread(() => AccountRefreshed?.Invoke(this, EventArgs.Empty));

                return me;
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (_sync) _inFlight = null;
            }
        }

        public void OnForeground()
        {
            if (_foreground) return;
            _foreground = true;

            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = new CancellationTokenSource();
            _ = PollAsync(_pollCts.Token);
        }

        public void OnBackground()
        {
            _foreground = false;
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }

        private async Task PollAsync(CancellationToken ct)
        {
            try
            {
                // Resuming is itself a reason to ask: the grant may have landed while the
                // app was away.
                await RefreshNowAsync(ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(NextInterval(), ct).ConfigureAwait(false);
                    await RefreshNowAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* went to background */ }
            catch { /* a poll loop must never take the app down */ }
        }

        private TimeSpan NextInterval()
        {
            if (_auth.SubscriptionState != SubscriptionState.Active) return WaitingInterval;

            var expiry = _auth.CurrentUser?.expiresAt;
            if (expiry is null) return WaitingInterval;

            var left = expiry.Value.ToUniversalTime() - DateTime.UtcNow;
            return left <= ExpiringSoon ? WaitingInterval : SettledInterval;
        }

        public void Dispose()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
        }
    }
}
