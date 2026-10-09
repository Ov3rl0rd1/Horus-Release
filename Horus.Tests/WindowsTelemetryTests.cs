using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Horus.Application;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Horus.Platforms.Windows.Views;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The numbers on the Windows home screen: the speed graph's window and axis, and the ping
/// to the node. A wrong axis makes an idle tunnel look busy; a probe that counts a closed
/// port's silence as loss tells a player their connection is broken when it is fine.
/// </summary>
public class TrafficHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 1_000_000)]                 // idle: the floor, not noise blown up to full height
    [InlineData(100_000, 1_000_000)]           // 0.8 Mbit/s
    [InlineData(162_500, 2_000_000)]           // 1.3 Mbit/s
    [InlineData(612_500, 5_000_000)]           // 4.9 Mbit/s
    [InlineData(637_500, 10_000_000)]          // 5.1 Mbit/s
    [InlineData(1_250_000, 10_000_000)]        // exactly 10 Mbit/s stays 10
    [InlineData(1_375_000, 20_000_000)]        // 11 Mbit/s
    [InlineData(32_500_000, 500_000_000)]      // 260 Mbit/s
    public void The_axis_rounds_up_to_one_two_or_five(double peakBytes, double ceilingBits)
    {
        Assert.Equal(ceilingBits / 8, TrafficHistory.CeilingFor(peakBytes), 3);
    }

    [Theory]
    [InlineData(125_000, "1 Мбит/с")]
    [InlineData(62_500, "500 Кбит/с")]
    [InlineData(250_000_000, "2 Гбит/с")]
    public void Axis_labels_are_in_bits(double bytes, string label)
    {
        Assert.Equal(label, TrafficHistory.AxisLabel(bytes).Replace(',', '.'));
    }

    [Fact]
    public void The_window_is_by_time_and_keeps_one_point_for_the_left_edge()
    {
        var h = new TrafficHistory();
        for (var s = 0; s <= 300; s += 15) h.Add(T0.AddSeconds(s), s, 0);   // a minimised window: every 15 s

        var now = T0.AddSeconds(300);
        var points = h.Window(now);
        Assert.Equal(0, points[^1].AgeSeconds);
        // One point beyond the span, so the line enters from the edge; the rest inside it.
        Assert.True(points[0].AgeSeconds > TrafficHistory.Span.TotalSeconds);
        Assert.All(points.Skip(1), p => Assert.True(p.AgeSeconds <= TrafficHistory.Span.TotalSeconds));
        Assert.Equal(10, points.Count);   // ages 135, 120, ... 0
    }

    [Fact]
    public void A_clock_step_backwards_starts_over_rather_than_drawing_backwards()
    {
        var h = new TrafficHistory();
        h.Add(T0, 1, 1);
        h.Add(T0.AddSeconds(1), 2, 2);
        h.Add(T0.AddSeconds(-30), 3, 3);
        var points = h.Window(T0);
        Assert.Single(points);
        Assert.Equal(3, points[0].DownBps);
    }
}

public class NodeLatencyMonitorTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(1);

    /// <summary>Runs the monitor against a scripted probe until it has consumed the script.</summary>
    private static async Task<(NodeLatencyMonitor Monitor, List<int> Ports)> Run(
        Func<int, int?> answer, int calls, Func<int, (string, int)?>? endpoint = null)
    {
        var ports = new List<int>();
        var done = new TaskCompletionSource();
        var n = 0;
        var monitor = new NodeLatencyMonitor(Fast, async (host, port, ifIndex, ct) =>
        {
            int index;
            lock (ports) { ports.Add(port); index = ports.Count; }
            // Past the script the probe hangs until stopped, so no extra sample sneaks in.
            if (index > calls) await Task.Delay(Timeout.Infinite, ct);
            return answer(port);
        });
        monitor.Updated += (_, _) => { if (Interlocked.Increment(ref n) == calls) done.TrySetResult(); };
        var asked = 0;
        monitor.Start(() => endpoint is null ? ("203.0.113.5", 8443) : endpoint(Interlocked.Increment(ref asked)), () => 0);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        monitor.Stop();
        await Task.Delay(20);
        lock (ports) return (monitor, ports.Take(calls).ToList());
    }

    [Fact]
    public async Task Silence_before_the_first_answer_moves_to_another_port_and_is_not_loss()
    {
        // The Hysteria2 port is UDP-only behind a firewall that drops; 443 answers.
        var (m, ports) = await Run(port => port == 443 ? 30 : null, calls: 6);

        Assert.Equal([8443, 8443, 443, 443, 443, 443], ports);
        Assert.True(m.Answering);
        Assert.Equal(443, m.Port);
        Assert.Equal(0, m.Loss);
        Assert.Equal(30, m.Typical);
    }

    [Fact]
    public async Task After_an_answer_a_miss_is_loss()
    {
        var script = new ConcurrentQueue<int?>([20, 30, null, 20, 30, null, 20, 30]);
        var (m, ports) = await Run(_ => script.TryDequeue(out var v) ? v : 25, calls: 8);

        Assert.All(ports, p => Assert.Equal(8443, p));   // answered, so it never moves
        Assert.Equal(2 / 8.0, m.Loss, 3);
        Assert.Equal(10, m.Jitter);                      // |30-20| every step
        Assert.Equal(30, m.Latest);
        Assert.Contains(m.Typical, new int?[] { 20, 30 });
    }

    [Fact]
    public async Task A_new_node_starts_a_new_history()
    {
        var (m, _) = await Run(_ => 40, calls: 5,
            endpoint: i => i <= 3 ? ("203.0.113.5", 443) : ("198.51.100.7", 443));
        Assert.Equal(2, m.Samples.Count);
    }
}

public class PhysicalProbeTests
{
    [Fact]
    public async Task An_open_port_answers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var ms = await PhysicalProbe.ConnectAsync("127.0.0.1", port, 0, TimeSpan.FromSeconds(2), false, default);
        Assert.NotNull(ms);
        Assert.True(ms >= 1);
    }

    [Fact]
    public async Task A_reset_counts_only_when_asked_to()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }

        Assert.NotNull(await PhysicalProbe.ConnectAsync("127.0.0.1", port, 0, TimeSpan.FromSeconds(2), true, default));
        Assert.Null(await PhysicalProbe.ConnectAsync("127.0.0.1", port, 0, TimeSpan.FromSeconds(2), false, default));
    }

    [Fact]
    public async Task An_unresolvable_name_is_no_answer_rather_than_an_exception()
    {
        Assert.Null(await PhysicalProbe.ConnectAsync("no-such-host.invalid", 443, 0, TimeSpan.FromSeconds(2), true, default));
    }
}

/// <summary>Static hook, so these share one class and never run in parallel with each other.</summary>
public class LatencyProbeHookTests
{
    [Fact]
    public async Task A_platform_connector_replaces_the_default_dial()
    {
        var asked = new List<(string, int)>();
        LatencyProbe.Connector = (host, port, ct) =>
        {
            lock (asked) asked.Add((host, port));
            return Task.FromResult<int?>(port == 443 ? null : 42);   // 443 filtered, 8443 answers
        };
        try
        {
            var servers = await LatencyProbe.MeasureAsync([new ServerInfo { Id = 1, Host = "node.example" }]);
            Assert.Equal(42, servers[0].PingMs);
            Assert.Equal([("node.example", 443), ("node.example", 8443)], asked);
        }
        finally { LatencyProbe.Connector = null; }
    }

    [Fact]
    public async Task Without_a_host_nothing_is_dialled()
    {
        LatencyProbe.Connector = (_, _, _) => throw new InvalidOperationException("must not dial");
        try
        {
            var servers = await LatencyProbe.MeasureAsync([new ServerInfo { Id = 1, Host = "" }]);
            Assert.Null(servers[0].PingMs);
        }
        finally { LatencyProbe.Connector = null; }
    }
}
