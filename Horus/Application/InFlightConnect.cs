namespace Horus.Application
{
    /// <summary>
    /// The connect in progress, so that a disconnect can stop it.
    ///
    /// <para>A connect takes seconds: the API, each offer proved in turn, the adapter. A
    /// disconnect pressed meanwhile used to wait for all of it to finish before doing
    /// anything (Windows), or tear the tunnel down underneath a connect that carried on and
    /// brought it back up (Android). <see cref="CancelAsync"/> cancels the connect and
    /// returns once it has unwound — its own teardown included — so the caller finds the
    /// controller settled.</para>
    /// </summary>
    public sealed class InFlightConnect
    {
        private sealed record Attempt(CancellationTokenSource Cancellation, Task Finished);

        private Attempt? _current;

        public bool IsRunning => Volatile.Read(ref _current) is not null;

        /// <summary>
        /// Runs <paramref name="connect"/> with a token that <see cref="CancelAsync"/>, as well
        /// as <paramref name="ct"/>, cancels. Whatever it throws is rethrown as is.
        /// </summary>
        public async Task RunAsync(Func<CancellationToken, Task> connect, CancellationToken ct)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempt = new Attempt(cancellation, finished.Task);
            Volatile.Write(ref _current, attempt);

            try
            {
                await connect(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _current, null, attempt);
                finished.TrySetResult();
            }
        }

        /// <summary>Cancels the connect in progress, if any, and waits for it to unwind. Never throws.</summary>
        public async Task CancelAsync()
        {
            if (Volatile.Read(ref _current) is not { } attempt) return;

            try { attempt.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* finished in the meantime */ }

            await attempt.Finished.ConfigureAwait(false);
        }
    }
}
