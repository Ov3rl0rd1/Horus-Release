using Horus.Domain.Models;

namespace Horus.Application.Update
{
    /// <summary>Why the updater is not acting right now. Logged, never shown to the user.</summary>
    public enum UpdateHold
    {
        /// <summary>Go ahead.</summary>
        None = 0,
        NoNetwork,
        /// <summary>A small update will not spend mobile data.</summary>
        WaitingForWifi,
        /// <summary>Waiting for night, charging and an idle screen.</summary>
        WaitingForQuietHours,
        /// <summary>The tunnel is up and this update is not worth interrupting it.</summary>
        WaitingForVpnOff
    }

    /// <summary>
    /// When to download and when to install. Pure and side-effect free so the whole policy
    /// can be tested without a device — which matters here, because every branch of it is
    /// something that either eats a user's mobile data or restarts their VPN at a bad
    /// moment, and neither is observable in a quick manual test.
    ///
    /// The shape of the policy is the product decision:
    /// <list type="bullet">
    /// <item><b>Immediate</b> (first or second version component changed) — fetch on
    /// whatever network is there, install as soon as it lands, even over a live tunnel.
    /// Wi-Fi is preferred for the first couple of hours purely to save data when it costs
    /// nothing to wait.</item>
    /// <item><b>Deferred</b> (third component only) — Wi-Fi only, and installed at a moment
    /// the user will not notice: night, on charge, screen off, tunnel down. If no such
    /// moment arrives within 24 hours, it is installed anyway.</item>
    /// </list>
    /// </summary>
    public static class UpdatePolicy
    {
        /// <summary>Local hours treated as "the user is asleep".</summary>
        public const int QuietStartHour = 1;
        public const int QuietEndHour = 6;

        /// <summary>
        /// How long an immediate update waits for Wi-Fi before accepting mobile data.
        /// Short, because the point of an immediate update is that it is needed.
        /// </summary>
        public static readonly TimeSpan MeteredGrace = TimeSpan.FromHours(2);

        public static bool IsQuietHour(int localHour) =>
            localHour >= QuietStartHour && localHour < QuietEndHour;

        /// <summary>Whether the payload may be fetched now.</summary>
        public static UpdateHold CanDownload(UpdatePlan plan, DeviceConditions device, DateTimeOffset nowUtc)
        {
            if (!device.HasNetwork) return UpdateHold.NoNetwork;
            if (device.IsUnmetered) return UpdateHold.None;

            // Metered from here down.
            return plan.Urgency == UpdateUrgency.Immediate && nowUtc - plan.FirstSeenUtc >= MeteredGrace
                ? UpdateHold.None
                : UpdateHold.WaitingForWifi;
        }

        /// <summary>
        /// Whether the downloaded payload may be handed to the installer now.
        /// <paramref name="localHour"/> is passed in rather than read from the clock so the
        /// decision does not depend on the time zone of whatever machine evaluates it.
        /// </summary>
        public static UpdateHold CanInstall(
            UpdatePlan plan, DeviceConditions device, bool vpnConnected, DateTimeOffset nowUtc, int localHour)
        {
            // The 24-hour window overrides everything, including a live tunnel. A user who
            // never turns the VPN off would otherwise never update — and that is precisely
            // the usage this product is designed around.
            if (plan.PastDeadline(nowUtc)) return UpdateHold.None;

            if (plan.Urgency == UpdateUrgency.Immediate) return UpdateHold.None;

            if (vpnConnected) return UpdateHold.WaitingForVpnOff;

            return IsQuietHour(localHour) && device.IsCharging && !device.IsInteractive
                ? UpdateHold.None
                : UpdateHold.WaitingForQuietHours;
        }

        /// <summary>
        /// How long to sleep before re-evaluating. Nothing pending means the check itself
        /// is the only cost, so it happens rarely; a pending deferred update has to sample
        /// often enough to notice the quiet window opening, but 15 minutes of a condition
        /// check is negligible next to a tunnel.
        /// </summary>
        public static TimeSpan NextPoll(UpdatePlan? plan) => plan?.Urgency switch
        {
            UpdateUrgency.Immediate => TimeSpan.FromMinutes(2),
            UpdateUrgency.Deferred => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromHours(6)
        };
    }
}
