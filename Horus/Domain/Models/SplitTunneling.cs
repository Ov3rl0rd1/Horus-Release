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
    }
}
