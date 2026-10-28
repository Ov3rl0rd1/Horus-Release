namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>A process as Windows lists it: cheap to read for every process there is.</summary>
    public readonly record struct ListedProcess(int Id, int ParentId, string Name);

    /// <summary>What takes opening the process to learn.</summary>
    public readonly record struct ProcessDetail(string Image, long StartedAt);

    /// <summary>
    /// The processes a chosen application started, and the ones they started in turn.
    ///
    /// <para>The core's process rule matches the executable that owns a connection, so an
    /// application that does its networking in a helper of its own — GitHub Desktop and its
    /// Copilot server, a launcher and its game, anything that runs git — took the user's choice
    /// only for its own executable, and the helper went the other way. The helpers are found by
    /// parent id and given to the core by full path. A bare name would take every copy of that
    /// executable along, whoever started it.</para>
    ///
    /// <para>Pure — <c>Horus.Tests</c> pins it.</para>
    /// </summary>
    public static class ProcessFamily
    {
        /// <summary>
        /// Full image paths, with forward slashes, of every process descending from a
        /// <paramref name="chosen"/> application. A descendant under
        /// <paramref name="systemFolder"/> is walked through but not named: <c>cmd.exe</c> or
        /// <c>conhost.exe</c> serve every program on the machine, and naming them would route
        /// all of those too.
        /// </summary>
        /// <param name="describe">Opens a process; null when it cannot be read.</param>
        public static IReadOnlyList<string> DescendantImages(
            IReadOnlyCollection<ListedProcess> processes,
            IReadOnlyCollection<string> chosen,
            Func<int, ProcessDetail?> describe,
            string systemFolder)
        {
            var chosenNames = ChosenNames(chosen);
            if (chosenNames.Count == 0) return [];

            var children = processes.ToLookup(p => p.ParentId);
            var system = Slashed(systemFolder).TrimEnd('/') + "/";
            var images = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<int>();
            var pending = new Queue<(ListedProcess Process, ProcessDetail Detail)>();

            foreach (var root in processes.Where(p => chosenNames.Contains(p.Name)))
            {
                if (visited.Add(root.Id) && describe(root.Id) is { } detail) pending.Enqueue((root, detail));
            }

            while (pending.TryDequeue(out var parent))
            {
                foreach (var child in children[parent.Process.Id])
                {
                    if (!visited.Add(child.Id)) continue;

                    // An id outlives its process and may be reused: a child older than its
                    // parent was started by an earlier owner of the id.
                    if (describe(child.Id) is not { } detail || detail.StartedAt < parent.Detail.StartedAt) continue;

                    var image = Slashed(detail.Image);
                    if (!chosenNames.Contains(child.Name) && !image.StartsWith(system, StringComparison.OrdinalIgnoreCase))
                        images.Add(image);

                    pending.Enqueue((child, detail));
                }
            }

            return [.. images];
        }

        /// <summary>Whether any chosen application is running.</summary>
        public static bool AnyRunning(IReadOnlyCollection<ListedProcess> processes, IReadOnlyCollection<string> chosen)
        {
            var chosenNames = ChosenNames(chosen);
            return processes.Any(p => chosenNames.Contains(p.Name));
        }

        /// <summary>
        /// The helpers to keep matching: every one seen since a chosen application started, for
        /// as long as one runs. A helper started for a second at a time — git, once per fetch —
        /// is then matched from its first packet, not from the next look at the process list.
        /// </summary>
        public static IReadOnlyList<string> Remember(
            IReadOnlyCollection<string> known, IReadOnlyCollection<string> found, bool chosenAppRunning) =>
            chosenAppRunning
                ? [.. new SortedSet<string>(known.Concat(found), StringComparer.OrdinalIgnoreCase)]
                : [];

        private static HashSet<string> ChosenNames(IReadOnlyCollection<string> chosen) =>
            chosen
                .Select(entry => Slashed(entry.Trim().Trim('"')))
                .Select(entry => entry[(entry.LastIndexOf('/') + 1)..])
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string Slashed(string path) => path.Replace('\\', '/');
    }
}
