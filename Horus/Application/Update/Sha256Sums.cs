using System.Globalization;
using System.Text.RegularExpressions;
using Horus.Domain.Models;

namespace Horus.Application.Update
{
    /// <summary>
    /// Reads the <c>SHA256SUMS.txt</c> the release workflow publishes beside the downloads:
    ///
    /// <code>
    /// ca6f077f…6ef9  Horus-0.1.0-android-arm64-v8a.apk
    /// cbc3541f…cf95  Horus-0.1.0-win-x64.msi
    /// </code>
    ///
    /// Two jobs. It supplies the digest every download is checked against, and — because
    /// the site serves the payloads under fixed, unversioned names — it is also the only
    /// place the site publishes <i>which</i> version those bytes are. That makes the file
    /// name format a contract: rename the artifacts in the release workflow and the update
    /// path stops seeing new versions, silently. <c>Horus.Tests/UpdateTests</c> pins it.
    /// </summary>
    public static partial class Sha256Sums
    {
        /// <summary>
        /// Matches the version in a published artifact name.
        ///
        /// Anchored on the platform token rather than on the shape of the version, because
        /// the two are ambiguous: a pattern that lets the version end in an optional
        /// <c>-label</c> reads <c>Horus-0.1.0-win-x64.msi</c> as version
        /// <c>0.1.0-win</c> — a valid pre-release, sorting <i>below</i> 0.1.0, so the
        /// updater would decide the release it is looking at is older than the build
        /// installed and quietly never update. Taking everything up to the first platform
        /// token instead keeps <c>0.2.0-rc1</c> working and leaves
        /// <see cref="AppVersion.TryParse"/> to reject anything that is not a version.
        /// </summary>
        [GeneratedRegex(@"^Horus-(.+?)-(?:win-x64|android-)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ArtifactName();

        /// <summary>
        /// Parses to file name → lower-case digest. Malformed lines are skipped rather
        /// than failing the whole file: a partially readable checksum list still lets the
        /// verifiable assets through, and an unverifiable one is rejected later anyway.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Parse(string? text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return map;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                // "<64 hex>  <name>" — coreutils writes two spaces, or a space and a
                // binary-mode '*'. Accept any run of whitespace.
                var split = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length != 2) continue;

                var digest = split[0];
                if (digest.Length != 64 || !IsHex(digest)) continue;

                var name = split[1].TrimStart('*', ' ').Trim();
                if (name.Length == 0) continue;

                map[name] = digest.ToLowerInvariant();
            }

            return map;
        }

        /// <summary>
        /// The release version, taken from the artifact names. Returns
        /// <see cref="AppVersion.Zero"/> when no name carries one — which is how a
        /// truncated or unrelated file fails closed instead of proposing a downgrade.
        /// </summary>
        public static AppVersion InferVersion(IEnumerable<string> fileNames)
        {
            var best = AppVersion.Zero;
            foreach (var name in fileNames)
            {
                var m = ArtifactName().Match(name);
                if (!m.Success) continue;
                if (AppVersion.TryParse(m.Groups[1].Value, out var v) && v > best) best = v;
            }
            return best;
        }

        private static bool IsHex(string s)
        {
            foreach (var c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>
        /// Finds the digest for a published asset by matching the tail of its name, so one
        /// checksum list serves both the versioned GitHub names and the unversioned ones
        /// on the site.
        /// </summary>
        public static string? DigestForSuffix(IReadOnlyDictionary<string, string> sums, string suffix)
        {
            foreach (var (name, digest) in sums)
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return digest;
            return null;
        }

        /// <summary>Formats a raw digest for comparison.</summary>
        public static string ToHex(ReadOnlySpan<byte> hash)
        {
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
