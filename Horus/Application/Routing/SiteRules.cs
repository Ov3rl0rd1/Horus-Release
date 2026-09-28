using Horus.Domain.Models;

namespace Horus.Application.Routing
{
    /// <summary>One site the user wants routed somewhere specific.</summary>
    /// <param name="Pattern">A domain expression the core understands, already normalised.</param>
    /// <param name="Action">Where its traffic goes.</param>
    public readonly record struct SiteRule(string Pattern, RuleAction Action);

    /// <summary>
    /// Turns what a user types into domain expressions the core will accept, and groups them
    /// by destination.
    ///
    /// <para><b>Why validation is not optional here.</b> These strings go straight into the
    /// routing section of the config. xray does not skip a rule it cannot parse — it rejects
    /// the whole config, <c>XrayStart</c> fails, and the tunnel never comes up. A user who
    /// types "https://youtube.com/" into a box and can no longer connect at all has been
    /// handed a way to break the app with a typo, so anything that will not parse is dropped
    /// here instead.</para>
    ///
    /// <para>Free of MAUI types on purpose: this is the part that has to be right, and the
    /// test project can only link files that are.</para>
    /// </summary>
    public static class SiteRules
    {
        /// <summary>
        /// Longest name the core will take. Well past any real hostname; the point is to
        /// refuse something pasted in by accident rather than to be exact.
        /// </summary>
        private const int MaxLength = 253;

        /// <summary>
        /// Normalises one user-entered line, or null when there is nothing usable in it.
        ///
        /// <para>Accepts what people actually type — a pasted URL, a leading dot, a stray
        /// "www." — and keeps the prefixes the core defines (<c>domain:</c>, <c>full:</c>,
        /// <c>keyword:</c>, <c>regexp:</c>, <c>geosite:</c>) untouched for anyone who knows
        /// them. A bare name becomes <c>domain:</c>, which matches the name and its
        /// subdomains: typing "youtube.com" and not having it cover "www.youtube.com" would
        /// be surprising in the wrong direction.</para>
        /// </summary>
        public static string? Normalise(string? input)
        {
            var text = input?.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            // An explicit prefix is the user saying they know the syntax. regexp: in
            // particular must survive untouched — everything below would corrupt it.
            foreach (var prefix in Prefixes)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return text.Length > prefix.Length ? text : null;
            }

            // A pasted URL is the common case and there is no reason to reject it.
            if (text.Contains("://", StringComparison.Ordinal))
                text = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];

            text = text.Split('/')[0].Split('?')[0].Split('#')[0];

            // Strip credentials and a port; neither means anything to a domain match.
            if (text.Contains('@')) text = text[(text.LastIndexOf('@') + 1)..];
            if (text.Contains(':')) text = text.Split(':')[0];

            text = text.Trim().Trim('.').ToLowerInvariant();

            return IsPlausibleHost(text) ? "domain:" + text : null;
        }

        private static readonly string[] Prefixes =
            ["domain:", "full:", "keyword:", "regexp:", "geosite:", "ext:"];

        /// <summary>
        /// Whether a bare string looks like a hostname. Deliberately loose — the core is the
        /// real authority and new TLDs appear constantly — but strict enough that free text
        /// ("мой банк", "192.168.0.1/24") does not reach the config.
        /// </summary>
        private static bool IsPlausibleHost(string text)
        {
            if (text.Length is 0 or > MaxLength) return false;
            if (!text.Contains('.')) return false;
            if (text.Contains("..", StringComparison.Ordinal)) return false;

            foreach (var c in text)
            {
                // Letters cover internationalised names, which the core accepts as UTF-8.
                if (char.IsLetterOrDigit(c) || c is '-' or '.' or '_') continue;
                return false;
            }

            // An address is an IP rule, not a domain one, and silently treating it as a
            // domain produces a rule that matches nothing.
            return !System.Net.IPAddress.TryParse(text, out _);
        }

        /// <summary>
        /// Parses a list of user lines into rules, dropping anything unusable and anything
        /// repeated. Order is preserved: the first mention of a pattern wins, so re-adding a
        /// site with a different destination does not silently change the earlier one.
        /// </summary>
        public static IReadOnlyList<SiteRule> Parse(IEnumerable<(string Pattern, RuleAction Action)> entries)
        {
            var rules = new List<SiteRule>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (pattern, action) in entries)
            {
                var normalised = Normalise(pattern);
                if (normalised is null || !seen.Add(normalised)) continue;

                rules.Add(new SiteRule(normalised, action));
            }

            return rules;
        }

        /// <summary>All the patterns headed for one destination, in order.</summary>
        public static IReadOnlyList<string> For(IEnumerable<SiteRule> rules, RuleAction action) =>
            [.. rules.Where(r => r.Action == action).Select(r => r.Pattern)];
    }
}
