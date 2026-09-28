namespace Horus.Application
{
    /// <summary>
    /// Builds the jump strip beside a long alphabetical list.
    ///
    /// <para>Separate from the view-model, and free of any MAUI type, because the bug this
    /// replaces was pure arithmetic and went unnoticed for want of anywhere to test it. The
    /// old version was <c>keys.Distinct().Take(28)</c>.</para>
    ///
    /// <para><b>Why twenty-eight was exactly the wrong number.</b> The list is sorted with the
    /// current culture, which on a Russian device orders the entire Latin run before the
    /// entire Cyrillic one. Twenty-eight slots is "#" plus A–Z plus one more — so the cut
    /// landed precisely where the Cyrillic letters begin, and every Russian-named app became
    /// unreachable. The single Cyrillic letter that did survive was whichever one the slot
    /// count reached, and А, В, Е, К, М, Н, О, Р, С, Т, У and Х are pixel-identical to their
    /// Latin counterparts. The strip therefore looked like an ordinary Latin alphabet in which
    /// one letter behaved oddly, rather than like a list that had been cut in half.</para>
    /// </summary>
    public static class AppListIndex
    {
        /// <summary>Stands in for letters there was no room for. Never a jump target.</summary>
        public const string Gap = "·";

        /// <summary>
        /// How many entries the strip may show. A phone list is around 500 px tall and an
        /// entry needs about fourteen, so beyond this the strip runs off the screen — which is
        /// what a naive concatenation of two alphabets does.
        /// </summary>
        public const int MaxSlots = 30;

        /// <summary>
        /// One entry per distinct key, in the order given, with a <see cref="Gap"/> wherever
        /// the alphabet changes or something had to be dropped.
        ///
        /// <para>Order is the caller's, not alphabetical: the strip has to agree with the list
        /// it scrolls, and the list is sorted by the platform's collation, which is not
        /// something to second-guess here.</para>
        /// </summary>
        public static IReadOnlyList<string> Build(IEnumerable<string> keys)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? lastScript = null;

            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

                var script = ScriptOf(key);

                // The two alphabets share a dozen shapes. Without a break between the runs,
                // the Cyrillic М sits in the strip looking exactly like the Latin M.
                if (lastScript is not null && script != lastScript) ordered.Add(Gap);

                ordered.Add(key);
                lastScript = script;
            }

            return Thin(ordered);
        }

        /// <summary>
        /// Which alphabet a key belongs to. Only used to tell runs apart, so being stable
        /// matters more than being exhaustive.
        /// </summary>
        public static string ScriptOf(string key) =>
            key.Length == 0 || !char.IsLetter(key[0]) ? "#"
            : key[0] >= '\u0400' && key[0] <= '\u04FF' ? "cyrillic"
            : "latin";

        /// <summary>
        /// Drops entries until the strip fits — evenly, never at the ends, and leaving a gap
        /// marker where something went.
        ///
        /// <para>Thinning rather than truncating is the whole point. What survives still spans
        /// the full list, so every part of it stays within a drag; truncation removes one end
        /// of the alphabet outright, and no amount of dragging brings it back.</para>
        /// </summary>
        private static IReadOnlyList<string> Thin(List<string> keys)
        {
            if (keys.Count <= MaxSlots) return keys;

            var kept = new List<string>(MaxSlots);
            var step = (double)(keys.Count - 1) / (MaxSlots - 1);
            var taken = -1;

            for (var slot = 0; slot < MaxSlots; slot++)
            {
                var index = (int)Math.Round(slot * step);
                if (index == taken) continue;
                taken = index;

                // Markers are sampled like everything else rather than inserted per skip.
                // Adding one at every gap in the sampling was the obvious thing to do and
                // silently doubled the strip's length — the budget is entries on screen, not
                // letters, and a marker takes a row just the same.
                var key = keys[index];
                if (key == Gap && (kept.Count == 0 || kept[^1] == Gap)) continue;

                kept.Add(key);
            }

            // Both ends must be real letters, or a drag cannot reach the first and last of
            // the list — which is the failure thinning exists to avoid.
            if (kept.Count > 0 && kept[^1] == Gap) kept.RemoveAt(kept.Count - 1);

            return kept;
        }
    }
}
