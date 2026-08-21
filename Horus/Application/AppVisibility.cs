using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// Whether a user is actually looking at the app.
    ///
    /// <para>Used to decide how often anything has to be sampled. A statistic nobody can
    /// see does not need to be a second old, and the difference between sampling at 1 Hz
    /// and at 1/60 Hz over a night is the difference between a VPN people keep on and one
    /// they blame for their battery.</para>
    ///
    /// <para>Static because the consumers — the traffic monitor, the health loop — are
    /// singletons started before any window exists and outliving every one of them.
    /// NekoBox draws the same distinction with two connection ids, foreground and
    /// background, and skips the IPC entirely for the background one.</para>
    /// </summary>
    public static class AppVisibility
    {
        private static int _foreground;

        /// <summary>A window is on screen and receiving input.</summary>
        public static bool IsForeground => Volatile.Read(ref _foreground) != 0;

        /// <summary>Raised when the app comes to the front, so paced loops can sample now
        /// rather than waiting out an interval chosen while nobody was watching.</summary>
        public static event EventHandler? BecameForeground;

        public static void SetForeground()
        {
            if (Interlocked.Exchange(ref _foreground, 1) == 1) return;
            Diag.Trace("app", "foreground");
            try { BecameForeground?.Invoke(null, EventArgs.Empty); } catch { }
        }

        public static void SetBackground()
        {
            if (Interlocked.Exchange(ref _foreground, 0) == 0) return;
            Diag.Trace("app", "background");
        }
    }
}
