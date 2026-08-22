using Horus.Domain.Models;

namespace Horus.Application
{
    /// <summary>
    /// The handful of settings that have to be readable from outside the ViewModel that
    /// edits them — the boot receiver, the tunnel service and the diagnostics snapshot all
    /// need them, and none of those has a DI scope to resolve into.
    ///
    /// <para>Deliberately not an interface with an implementation: these are three booleans
    /// backed by <c>Preferences</c>, and a service registration would only add a lookup
    /// between the caller and the value.</para>
    /// </summary>
    public static class UserPreferences
    {
        private const string AutoStartKey = "horus.pref.autostart";
        private const string AutoConnectKey = "horus.pref.autoconnect";
        private const string VerboseLogKey = "horus.pref.verboselog";

        /// <summary>
        /// Bring the tunnel back after a reboot. Read by <c>BootReceiver</c>, which also
        /// stands down when Always-on VPN is enabled — the OS does a better job of it and
        /// two mechanisms racing is worse than either alone.
        ///
        /// <para>Defaults to true: someone who leaves a VPN on for weeks expects a reboot
        /// to be invisible, and the receiver still refuses to act unless
        /// <see cref="VpnIntent.Active"/> says the user had it on.</para>
        /// </summary>
        public static bool AutoStartOnBoot
        {
            get => Get(AutoStartKey, true);
            set => Set(AutoStartKey, value);
        }

        /// <summary>
        /// Connect on every launch, <b>including</b> one after an explicit disconnect.
        ///
        /// <para>Deliberately narrower than it sounds, because restoring a tunnel the user
        /// never turned off is a separate thing and is not gated on this — see
        /// <c>VpnManager.TryRestoreOrAutoConnectAsync</c>. What this adds is "always start
        /// protected", which only matters for someone who did disconnect.</para>
        ///
        /// <para>Defaults to false: a launch is a deliberate act and hijacking it surprises
        /// people who opened the app to change a setting.</para>
        /// </summary>
        public static bool AutoConnectOnLaunch
        {
            get => Get(AutoConnectKey, false);
            set => Set(AutoConnectKey, value);
        }

        /// <summary>
        /// Verbose diagnostics: <see cref="DiagLevel.Trace"/> in the event log, <c>info</c>
        /// out of hev-socks5-tunnel and <c>debug</c> out of xray.
        ///
        /// <para>Off by default and deliberately user-visible. At the shipping level a
        /// tunnel that connects and carries nothing writes almost nothing, so the one
        /// setting that makes a field failure explicable has to be reachable without a new
        /// build. Takes effect on the next connect, because both native components read
        /// their level from a config rendered at start-up.</para>
        /// </summary>
        public static bool VerboseLogging
        {
            get => Get(VerboseLogKey, false);
            set
            {
                Set(VerboseLogKey, value);
                ApplyLogLevel();
            }
        }

        /// <summary>
        /// Pushes <see cref="VerboseLogging"/> into <see cref="Diag.MinLevel"/>. Call once at
        /// startup; the setter handles later changes.
        /// </summary>
        public static void ApplyLogLevel() =>
            Diag.MinLevel = VerboseLogging ? DiagLevel.Trace : DiagLevel.Info;

        /// <summary>
        /// hev-socks5-tunnel's level. At <c>warn</c> a healthy-looking but dead tunnel
        /// writes an empty log, which is indistinguishable from "hev never started" — so
        /// the verbose setting exists mostly for this.
        /// </summary>
        public static string HevLogLevel => VerboseLogging ? "info" : "warn";

        /// <summary>
        /// xray's level. <c>info</c> even normally, because a failing outbound reports
        /// itself at info and at <c>warning</c> the log holds nothing but the startup
        /// banner. Access logging — the part that would record where the user goes —
        /// stays off at every level.
        /// </summary>
        public static string XrayLogLevel => VerboseLogging ? "debug" : "info";

        internal static IEnumerable<KeyValuePair<string, string?>> Describe()
        {
            yield return new("autoStartOnBoot", AutoStartOnBoot.ToString());
            yield return new("autoConnectOnLaunch", AutoConnectOnLaunch.ToString());
            yield return new("verboseLogging", VerboseLogging.ToString());
        }

        private static bool Get(string key, bool fallback)
        {
            try { return Preferences.Get(key, fallback); } catch { return fallback; }
        }

        private static void Set(string key, bool value)
        {
            try { Preferences.Set(key, value); } catch { }
        }
    }
}
