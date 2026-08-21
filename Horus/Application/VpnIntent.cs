using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// Whether the user wants the VPN on — persisted, so it survives the process dying.
    ///
    /// <para><b>The distinction this draws is the whole point.</b> "Is the tunnel up right
    /// now" is a runtime fact and lives in <c>VpnManager.State</c>. "Did the user ask for a
    /// tunnel" is an instruction, and it has to outlive the thing carrying it out —
    /// otherwise a process killed at 3 a.m. comes back with no idea it was supposed to be
    /// doing anything, which is exactly how a VPN silently stops working overnight.</para>
    ///
    /// <para>Every automatic path is gated on this: the sticky service restart, the boot
    /// receiver, the after-update reconnect. None of them can turn the VPN on for a user
    /// who turned it off, and all of them turn it back on for a user who did not.</para>
    ///
    /// <para>Rethink calls this <c>persistentState.vpnEnabled</c> / <c>activationRequested</c>
    /// and treats it as the authority its whole recovery machinery reads from.</para>
    /// </summary>
    public static class VpnIntent
    {
        private const string ActiveKey = "horus.vpn.intent.active";
        private const string SinceKey = "horus.vpn.intent.since";

        /// <summary>Restart bookkeeping, so a service that cannot come up stops trying.</summary>
        private const string RestartCountKey = "horus.vpn.restart.count";
        private const string RestartWindowKey = "horus.vpn.restart.window";

        /// <summary>
        /// More sticky restarts than this inside <see cref="RestartWindow"/> and the service
        /// stops resurrecting itself. Without a limit a connect that fails immediately turns
        /// into a restart loop that drains the battery faster than any leak this document
        /// set out to fix — the same reasoning as NekoBox refusing to restart a child
        /// process that exits within a second of starting.
        /// </summary>
        private const int MaxRestartsPerWindow = 4;

        private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

        /// <summary>
        /// True between an explicit connect and an explicit disconnect, across process
        /// lifetimes and reboots.
        /// </summary>
        public static bool Active
        {
            get
            {
                try { return Preferences.Get(ActiveKey, false); }
                catch { return false; }
            }
        }

        /// <summary>When the current intent was recorded. Null when inactive.</summary>
        public static DateTimeOffset? Since
        {
            get
            {
                try
                {
                    var ticks = Preferences.Get(SinceKey, 0L);
                    return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
                }
                catch { return null; }
            }
        }

        /// <summary>The user asked for a tunnel. Clears the restart budget.</summary>
        public static void Set()
        {
            try
            {
                Preferences.Set(ActiveKey, true);
                Preferences.Set(SinceKey, DateTimeOffset.UtcNow.UtcTicks);
                ResetRestartBudget();
                Diag.Info("intent", "vpn intent set");
            }
            catch (Exception ex) { Diag.Warn("intent", $"could not persist intent: {ex.Message}"); }
        }

        /// <summary>
        /// The user asked for no tunnel. Also used for a revoke — the consent is gone, so
        /// coming back automatically would mean fighting whichever app now holds the VPN slot.
        /// </summary>
        public static void Clear()
        {
            try
            {
                Preferences.Set(ActiveKey, false);
                Preferences.Remove(SinceKey);
                ResetRestartBudget();
                Diag.Info("intent", "vpn intent cleared");
            }
            catch (Exception ex) { Diag.Warn("intent", $"could not clear intent: {ex.Message}"); }
        }

        /// <summary>
        /// Records a system-initiated restart attempt and reports whether another one is
        /// allowed. Returns false once the budget for the current window is spent, which is
        /// the signal to stop and tell the user rather than keep cycling.
        /// </summary>
        public static bool TryConsumeRestart()
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var windowTicks = Preferences.Get(RestartWindowKey, 0L);
                var windowStart = windowTicks == 0
                    ? now
                    : new DateTimeOffset(windowTicks, TimeSpan.Zero);

                var count = Preferences.Get(RestartCountKey, 0);

                if (windowTicks == 0 || now - windowStart > RestartWindow)
                {
                    windowStart = now;
                    count = 0;
                }

                if (count >= MaxRestartsPerWindow)
                {
                    Diag.Warn("intent",
                        $"restart budget spent ({count}/{MaxRestartsPerWindow} in " +
                        $"{RestartWindow.TotalMinutes:F0} min) — standing down");
                    return false;
                }

                Preferences.Set(RestartWindowKey, windowStart.UtcTicks);
                Preferences.Set(RestartCountKey, count + 1);
                Diag.Info("intent", $"system restart {count + 1}/{MaxRestartsPerWindow}");
                return true;
            }
            catch
            {
                // Unreadable preferences must not disable recovery entirely.
                return true;
            }
        }

        /// <summary>Called on a successful connect: the service is evidently viable again.</summary>
        public static void ResetRestartBudget()
        {
            try
            {
                Preferences.Remove(RestartCountKey);
                Preferences.Remove(RestartWindowKey);
            }
            catch { }
        }

        internal static IEnumerable<KeyValuePair<string, string?>> Describe()
        {
            yield return new("active", Active.ToString());
            yield return new("since", Since?.ToLocalTime().ToString("dd.MM HH:mm:ss"));
            yield return new("restartsInWindow", SafeGet(RestartCountKey).ToString());
        }

        private static int SafeGet(string key)
        {
            try { return Preferences.Get(key, 0); } catch { return 0; }
        }
    }
}
