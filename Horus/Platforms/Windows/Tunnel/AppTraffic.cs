namespace Horus.Platforms.Windows.Tunnel
{
    /// <summary>The process behind a connection.</summary>
    public sealed record ProcessIdentity(int Pid, string Name, string? Path);

    /// <summary>How one application's traffic is routed right now.</summary>
    /// <param name="Name">Executable name, e.g. <c>cs2.exe</c>; <c>?</c> when the owner could not be found.</param>
    /// <param name="ViaVpn">Connections the core sent through the proxy.</param>
    /// <param name="Direct">Connections the core sent out directly, past the VPN.</param>
    /// <param name="Other">DNS, blocked, or not yet routed.</param>
    /// <param name="ConnectionIds">What to close to make the application reconnect under the current rules.</param>
    /// <param name="Targets">A few destinations, for the user to recognise the traffic by.</param>
    public sealed record AppTraffic(
        string Name, string? Path, IReadOnlyList<int> Pids,
        int ViaVpn, int Direct, int Other,
        long Up, long Down,
        IReadOnlyList<ulong> ConnectionIds,
        IReadOnlyList<string> Targets)
    {
        public int Total => ViaVpn + Direct + Other;

        /// <summary>Only through the VPN, only past it, or some of each.</summary>
        public RouteMix Mix => (ViaVpn > 0, Direct > 0) switch
        {
            (true, false) => RouteMix.Vpn,
            (false, true) => RouteMix.Direct,
            (true, true) => RouteMix.Mixed,
            _ => RouteMix.Other
        };
    }

    public enum RouteMix { Vpn, Direct, Mixed, Other }

    /// <summary>
    /// Folds the core's per-connection list into per-application rows.
    ///
    /// <para>Pure — <c>Horus.Tests</c> pins it — with the process lookup passed in, since
    /// that part needs the Windows connection tables.</para>
    /// </summary>
    public static class AppTrafficGrouping
    {
        public const string Unknown = "?";
        private const int MaxTargets = 5;

        public static IReadOnlyList<AppTraffic> Group(
            IReadOnlyList<CoreConnection> connections, Func<CoreConnection, ProcessIdentity?> owner)
        {
            var groups = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in connections)
            {
                var who = owner(c);
                var key = who?.Name ?? Unknown;
                if (!groups.TryGetValue(key, out var acc))
                    groups[key] = acc = new Acc(key, who?.Path);

                if (who is not null) acc.Pids.Add(who.Pid);
                switch (c.Outbound)
                {
                    case WindowsTunnelConfig.ProxyTag: acc.Vpn++; break;
                    case WindowsTunnelConfig.DirectTag: acc.Direct++; break;
                    default: acc.Other++; break;
                }
                acc.Up += c.Up;
                acc.Down += c.Down;
                acc.Ids.Add(c.Id);

                var target = HostOf(c.Target ?? c.Destination);
                if (acc.Targets.Count < MaxTargets && target.Length > 0 && !acc.Targets.Contains(target))
                    acc.Targets.Add(target);
            }

            return groups.Values
                .Select(a => new AppTraffic(a.Name, a.Path, [.. a.Pids.Order()], a.Vpn, a.Direct, a.Other,
                    a.Up, a.Down, a.Ids, a.Targets))
                // The busiest first: that is what someone checking "is my game on the VPN" is
                // looking at. Unknown owners last, they cannot be acted on.
                .OrderBy(a => a.Name == Unknown)
                .ThenByDescending(a => a.Up + a.Down)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The host part of <c>host:port</c>, IPv6 brackets included.</summary>
        public static string HostOf(string endpoint)
        {
            if (endpoint.StartsWith('['))
            {
                var close = endpoint.IndexOf(']');
                return close > 0 ? endpoint[1..close] : endpoint;
            }
            var colon = endpoint.LastIndexOf(':');
            return colon > 0 && endpoint.IndexOf(':') == colon ? endpoint[..colon] : endpoint;
        }

        private sealed class Acc(string name, string? path)
        {
            public string Name { get; } = name;
            public string? Path { get; } = path;
            public HashSet<int> Pids { get; } = [];
            public List<ulong> Ids { get; } = [];
            public List<string> Targets { get; } = [];
            public int Vpn, Direct, Other;
            public long Up, Down;
        }
    }
}
