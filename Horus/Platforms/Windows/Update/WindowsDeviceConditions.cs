using System.Runtime.InteropServices;
using Horus.Domain.Interfaces;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Update
{
    /// <summary>
    /// Desktop answers to the same four questions the Android policy asks.
    ///
    /// Two of them mean something slightly different here and are answered accordingly: a
    /// desktop with no battery is always "charging", because there is no power budget to
    /// protect, and "interactive" is derived from input idle time rather than a screen
    /// state, because a Windows machine with the monitor asleep and the user away is
    /// exactly the moment an update should go in.
    /// </summary>
    public sealed class WindowsDeviceConditions : IDeviceConditions
    {
        /// <summary>No keyboard or mouse for this long counts as the user being away.</summary>
        private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(10);

        public DeviceConditions Read()
        {
            try
            {
                return new DeviceConditions(
                    HasNetwork: System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(),
                    IsUnmetered: IsUnmetered(),
                    IsCharging: IsOnMains(),
                    IsInteractive: IdleTime() < IdleThreshold);
            }
            catch
            {
                return DeviceConditions.Unknown;
            }
        }

        /// <summary>
        /// Windows marks a connection metered per profile — a phone hotspot, most often.
        /// An unreadable answer is treated as unmetered: on a desktop the usual case by
        /// far is wired or home Wi-Fi, and blocking every update on an unreadable API
        /// would be worse than occasionally spending a tethered megabyte.
        /// </summary>
        private static bool IsUnmetered()
        {
            try
            {
                var profile = global::Windows.Networking.Connectivity
                    .NetworkInformation.GetInternetConnectionProfile();
                if (profile is null) return true;

                var cost = profile.GetConnectionCost();
                return cost.NetworkCostType is
                    global::Windows.Networking.Connectivity.NetworkCostType.Unrestricted or
                    global::Windows.Networking.Connectivity.NetworkCostType.Unknown;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsOnMains()
        {
            if (!GetSystemPowerStatus(out var status)) return true;

            // BatteryFlag 128 = "no system battery" — a desktop, which is never on battery.
            if (status.BatteryFlag == 128) return true;
            return status.ACLineStatus == 1;
        }

        private static TimeSpan IdleTime()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

            // Both are 32-bit millisecond tick counts and wrap every ~49 days; unchecked
            // subtraction gives the right answer across the wrap.
            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    }
}
