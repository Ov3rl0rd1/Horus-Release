namespace Horus.Domain.Models
{
    public enum SplitTunnelingMode
    {
        Disabled,
        /// <summary>Only listed apps/processes go through VPN; everything else is direct.</summary>
        Whitelist,
        /// <summary>Listed apps/processes bypass VPN; everything else goes through it.</summary>
        Blacklist
    }

    public class AppOrProcessEntry
    {
        public string Id { get; set; } = string.Empty;       // package name (Android) or exe name (Windows)
        public string DisplayName { get; set; } = string.Empty;
        public string? IconPath { get; set; }                // optional, Windows only
        public bool IsSystem { get; set; }

        /// <summary>
        /// At least one instance has a window on screen.
        ///
        /// <para>The distinction a desktop process list lives or dies by. Windows is running
        /// two to three hundred processes at any moment and perhaps fifteen of them are things
        /// the user would recognise; an alphabetical list of the rest is not a picker, it is a
        /// haystack. Always false on Android, where every entry is an installed app and the
        /// question does not arise.</para>
        /// </summary>
        public bool HasWindow { get; set; }

        /// <summary>Full image path, when the platform can supply one. Shown as the subtitle.</summary>
        public string? Path { get; set; }
    }
}
