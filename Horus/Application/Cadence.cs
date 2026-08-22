namespace Horus.Application
{
    /// <summary>
    /// Timing helpers shared by the paced loops.
    ///
    /// <para>Both the traffic sampler and the health monitor run on an interval that
    /// changes with how interesting the moment is, and both had the same two problems: a
    /// plain <c>Task.Delay(interval)</c> accumulates drift, and it cannot be shortened when
    /// the reason for the long interval goes away — the user picking the phone up would sit
    /// through the rest of a ninety-second sleep chosen while the screen was off.</para>
    /// </summary>
    public static class Cadence
    {
        /// <summary>
        /// The next multiple of <paramref name="intervalMs"/> after <paramref name="nowMs"/>.
        ///
        /// <para>Aligning wakeups to a shared boundary lets the platform coalesce them —
        /// with other apps and with its own work — instead of waking the CPU at whatever
        /// offset each loop happened to start at. Rethink's keepalive scheduler does this
        /// and then advances by whole intervals rather than re-delaying, so a slow tick
        /// does not push every subsequent one out of alignment.</para>
        /// </summary>
        public static long AlignToNext(long nowMs, long intervalMs) =>
            intervalMs <= 0 ? nowMs : ((nowMs / intervalMs) + 1) * intervalMs;

        /// <summary>Milliseconds on the monotonic clock. Never <c>DateTime.UtcNow</c>: an
        /// NTP correction on a device that has been up for weeks would move every deadline
        /// at once.</summary>
        public static long NowMs() => Environment.TickCount64;

        /// <summary>
        /// Waits until the aligned deadline, or until <paramref name="wake"/> is completed,
        /// whichever comes first. Returns false when cancelled.
        /// </summary>
        public static async Task<bool> WaitUntilAsync(
            long deadlineMs, Task wake, CancellationToken ct)
        {
            var remaining = deadlineMs - NowMs();
            if (remaining <= 0) return !ct.IsCancellationRequested;

            try
            {
                await Task.WhenAny(wake, Task.Delay((int)Math.Min(remaining, int.MaxValue), ct))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }

            return !ct.IsCancellationRequested;
        }
    }
}
