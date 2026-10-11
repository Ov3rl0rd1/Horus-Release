using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// When the Windows client is allowed to suspect its own tunnel. A false "stalled" costs
/// a probe; it must never be cheap enough to cost a reconnect, which is every
/// connection on the machine dropped.
/// </summary>
public class ProxyStallDetectorTests
{
    private static CoreConnection C(ulong id, long up, long down, long startedMs, string outbound = "proxy") =>
        new() { Id = id, Network = "tcp", Outbound = outbound, Up = up, Down = down, StartedMs = startedMs };

    [Fact]
    public void The_first_look_has_no_baseline()
    {
        var d = new ProxyStallDetector();
        Assert.Equal(StallVerdict.Idle, d.Feed([C(1, 100_000, 0, 0)], 60_000));
    }

    [Fact]
    public void Bytes_coming_back_are_health()
    {
        var d = new ProxyStallDetector();
        d.Feed([C(1, 1000, 10, 0)], 10_000);
        Assert.Equal(StallVerdict.Healthy, d.Feed([C(1, 50_000, 11, 0)], 20_000));
    }

    [Fact]
    public void Sending_into_silence_twice_is_a_stall()
    {
        var d = new ProxyStallDetector();
        d.Feed([C(1, 1000, 0, 0), C(2, 1000, 0, 0)], 10_000);
        Assert.Equal(StallVerdict.Suspect, d.Feed([C(1, 6000, 0, 0), C(2, 6000, 0, 0)], 20_000));
        Assert.Equal(StallVerdict.Stalled, d.Feed([C(1, 12_000, 0, 0), C(2, 12_000, 0, 0)], 30_000));
    }

    [Fact]
    public void One_unanswered_upload_is_not_enough()
    {
        // A single long upload gets almost nothing back; that is an upload, not a dead proxy.
        var d = new ProxyStallDetector();
        d.Feed([C(1, 1000, 0, 0)], 10_000);
        Assert.Equal(StallVerdict.Idle, d.Feed([C(1, 500_000, 0, 0)], 20_000));
        Assert.Equal(StallVerdict.Idle, d.Feed([C(1, 900_000, 0, 0)], 30_000));
    }

    [Fact]
    public void Young_connections_are_given_time()
    {
        var d = new ProxyStallDetector();
        d.Feed([], 10_000);
        Assert.Equal(StallVerdict.Idle, d.Feed([C(1, 8000, 0, 9_000), C(2, 8000, 0, 9_000)], 12_000));
    }

    [Fact]
    public void Direct_traffic_says_nothing_about_the_proxy()
    {
        var d = new ProxyStallDetector();
        d.Feed([C(1, 1000, 0, 0, "direct"), C(2, 1000, 0, 0, "direct")], 10_000);
        Assert.Equal(StallVerdict.Idle, d.Feed([C(1, 90_000, 0, 0, "direct"), C(2, 90_000, 0, 0, "direct")], 20_000));
    }

    [Fact]
    public void Health_in_between_starts_the_count_again()
    {
        var d = new ProxyStallDetector();
        d.Feed([C(1, 1000, 0, 0), C(2, 1000, 0, 0)], 10_000);
        Assert.Equal(StallVerdict.Suspect, d.Feed([C(1, 6000, 0, 0), C(2, 6000, 0, 0)], 20_000));
        Assert.Equal(StallVerdict.Healthy, d.Feed([C(1, 7000, 5, 0), C(2, 6000, 0, 0)], 30_000));
        Assert.Equal(StallVerdict.Suspect, d.Feed([C(1, 14_000, 5, 0), C(2, 12_000, 0, 0), C(3, 1000, 0, 0)], 40_000));
    }

    [Fact]
    public void A_closed_connection_does_not_count_as_negative_traffic()
    {
        var d = new ProxyStallDetector();
        d.Feed([C(1, 50_000, 50_000, 0)], 10_000);
        Assert.Equal(StallVerdict.Idle, d.Feed([], 20_000));
    }

    [Fact]
    public void Parses_what_the_core_returns()
    {
        var list = CoreConnection.Parse("""
            [{"down":38,"dst":"10.99.1.1:7","id":1,"net":"tcp","outbound":"proxy","src":"198.18.0.1:52324","startedMs":1791562544565,"up":38},
             {"down":0,"dst":"1.2.3.4:27015","id":2,"net":"udp","src":"198.18.0.1:50000","startedMs":1,"up":9,"target":"x.test:27015"}]
            """);
        Assert.Equal(2, list.Count);
        Assert.Equal(52324, list[0].SourcePort);
        Assert.Null(list[1].Outbound);
        Assert.Equal("x.test:27015", list[1].Target);
        Assert.Empty(CoreConnection.Parse("not json"));
        Assert.Empty(CoreConnection.Parse(null));
    }
}
