using Horus.Domain.Models;
using System.Diagnostics;
using System.Net.Sockets;

namespace Horus.Application
{
    /// <summary>
    /// Measures how far away each candidate node is, by timing a TCP connect.
    ///
    /// <para><b>Why the client measures and not the API.</b> The API can only see its own
    /// latency to a node, which says nothing about a user three thousand kilometres away
    /// on a mobile network. That is the whole reason <c>GET /servers</c> returns a list of
    /// candidates instead of a decision — the measurement has to happen where the user is.</para>
    ///
    /// <para><b>Why TCP connect and not ICMP.</b> Ping is unusable here: many hosts and
    /// most mobile carriers drop or deprioritise ICMP, so a silent node is
    /// indistinguishable from a far one, and Android needs no privileges for a TCP connect
    /// but does for a raw socket. Timing the handshake to a port the node actually serves
    /// also measures the thing that matters — whether the node answers <i>this</i> user —
    /// rather than whether a router along the way felt like replying.</para>
    ///
    /// <para>Probes run concurrently and the whole batch is bounded, because this sits in
    /// front of a screen the user is waiting on. A node that does not answer within
    /// <see cref="Timeout"/> is reported as null rather than as a large number: "did not
    /// answer" and "answered slowly" are different facts and the UI shows them differently.</para>
    /// </summary>
    public static class LatencyProbe
    {
        /// <summary>
        /// Ports tried, in order, until one answers.
        ///
        /// <para>443 first because every node serves REALITY there and middleboxes leave it
        /// alone. 8443 is the usual Hysteria2 port and is the fallback for a node whose 443
        /// is filtered on the path — measuring that path is more honest than declaring the
        /// node dead.</para>
        /// </summary>
        private static readonly int[] Ports = [443, 8443];

        /// <summary>
        /// Per-probe budget. Long enough for a slow mobile handshake to a distant node,
        /// short enough that a full sweep stays inside what someone will wait for.
        /// </summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Ceiling for the whole sweep regardless of how many candidates there are. They
        /// run concurrently, so this only bites when a lot of them are timing out at once.
        /// </summary>
        public static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(6);

        /// <summary>
        /// Fills <see cref="ServerInfo.PingMs"/> on every candidate, in place, and returns
        /// them sorted fastest-first with unreachable nodes last.
        ///
        /// <para>Never throws: a probe that fails is an answer about that node, not an
        /// error for the caller. Cancellation leaves whatever was measured so far.</para>
        /// </summary>
        public static async Task<IReadOnlyList<ServerInfo>> MeasureAsync(
            IEnumerable<ServerInfo> candidates, CancellationToken ct = default)
        {
            var list = candidates as IList<ServerInfo> ?? [.. candidates];
            if (list.Count == 0) return [];

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(BatchTimeout);

            try
            {
                await Task.WhenAll(list.Select(s => MeasureOneAsync(s, budget.Token)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* keep partial results */ }
            catch (Exception ex) { Diag.Warn("ping", $"sweep failed: {ex.Message}"); }

            var reachable = list.Count(s => s.PingMs is not null);
            Diag.Info("ping", $"probed {list.Count} node(s), {reachable} answered");

            return [.. Ordered(list)];
        }

        /// <summary>
        /// Fastest first, unreachable last, ties broken by how full the node is.
        ///
        /// <para>Capacity is the tie-breaker rather than the primary key on purpose: a node
        /// 40 ms closer is worth more to the user than one with a few more free slots, and
        /// the API has already excluded anything actually full.</para>
        /// </summary>
        public static IEnumerable<ServerInfo> Ordered(IEnumerable<ServerInfo> servers) =>
            servers
                .OrderBy(s => s.PingMs is null)
                .ThenBy(s => s.PingMs ?? int.MaxValue)
                .ThenBy(s => s.CurrentLoad);

        private static async Task MeasureOneAsync(ServerInfo server, CancellationToken ct)
        {
            server.PingMs = null;

            if (string.IsNullOrWhiteSpace(server.Host)) return;

            foreach (var port in Ports)
            {
                if (ct.IsCancellationRequested) return;

                var ms = await ConnectAsync(server.Host, port, ct).ConfigureAwait(false);
                if (ms is null) continue;

                server.PingMs = ms;
                Diag.Trace("ping", $"{server.Host}:{port} -> {ms} ms");
                return;
            }

            Diag.Trace("ping", $"{server.Host} did not answer");
        }

        /// <summary>Milliseconds to complete the handshake, or null if it did not.</summary>
        private static async Task<int?> ConnectAsync(string host, int port, CancellationToken ct)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(Timeout);

            var clock = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
                clock.Stop();

                // Sub-millisecond means a local answer rather than a real path; report 1 so
                // it sorts as fast without claiming an impossible zero.
                return Math.Max(1, (int)clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { return null; } // timed out, or caller gave up
            catch (SocketException) { return null; }            // refused, unreachable, no DNS
            catch (Exception) { return null; }
        }
    }
}
