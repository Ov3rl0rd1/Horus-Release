using System.Globalization;

namespace Horus.Domain.Models
{
    /// <summary>
    /// A three-part release version, as it appears in a git tag (<c>v0.1.0</c>), in
    /// <c>ApplicationDisplayVersion</c> (<c>0.9.0</c>) and inside a release asset name
    /// (<c>Horus-0.1.0-win-x64.msi</c>).
    ///
    /// Not a general semver implementation — only what the update path actually compares.
    /// Build metadata after <c>+</c> is dropped, since the assembly's informational version
    /// carries a commit hash there and it must not affect ordering. A pre-release label
    /// after <c>-</c> is kept and orders <i>below</i> the same release version, which is
    /// what makes a published <c>0.2.0</c> supersede a tester's <c>0.2.0-rc1</c>.
    /// </summary>
    public readonly record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease = null)
        : IComparable<AppVersion>
    {
        public static readonly AppVersion Zero = new(0, 0, 0);

        public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

        /// <summary>
        /// Parses <c>v0.1.0</c>, <c>0.1.0</c>, <c>0.1.0-rc.2</c> or <c>0.1.0+abc123</c>.
        /// A missing patch component reads as zero, so <c>v1.2</c> is <c>1.2.0</c>.
        /// </summary>
        public static bool TryParse(string? text, out AppVersion version)
        {
            version = Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

            // Build metadata never participates in ordering.
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];

            string? pre = null;
            var dash = s.IndexOf('-');
            if (dash >= 0)
            {
                pre = s[(dash + 1)..];
                s = s[..dash];
                if (pre.Length == 0) pre = null;
            }

            var parts = s.Split('.');
            if (parts.Length is < 2 or > 3) return false;

            if (!TryPart(parts[0], out var major)) return false;
            if (!TryPart(parts[1], out var minor)) return false;
            var patch = 0;
            if (parts.Length == 3 && !TryPart(parts[2], out patch)) return false;

            version = new AppVersion(major, minor, patch, pre);
            return true;

            static bool TryPart(string p, out int value) =>
                int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        public static AppVersion Parse(string? text) =>
            TryParse(text, out var v) ? v : Zero;

        public int CompareTo(AppVersion other)
        {
            var c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;

            // 1.0.0-rc1 < 1.0.0. Two pre-releases order by their labels, which is enough
            // to keep rc2 above rc1 without implementing semver's dot-segment rules.
            return (IsPreRelease, other.IsPreRelease) switch
            {
                (false, false) => 0,
                (true, false) => -1,
                (false, true) => 1,
                (true, true) => string.CompareOrdinal(PreRelease, other.PreRelease)
            };
        }

        public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
        public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
        public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
        public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

        /// <summary>
        /// How urgently <paramref name="candidate"/> should replace this version.
        ///
        /// The split is the product decision, not a semver one: a change in the first or
        /// second component is treated as something users need soon, a change in the third
        /// as something that can wait for a convenient moment.
        /// </summary>
        public UpdateUrgency UrgencyOf(AppVersion candidate)
        {
            if (candidate <= this) return UpdateUrgency.None;
            return candidate.Major != Major || candidate.Minor != Minor
                ? UpdateUrgency.Immediate
                : UpdateUrgency.Deferred;
        }

        public override string ToString() =>
            IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
    }
}
