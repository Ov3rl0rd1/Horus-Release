using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The Windows client's proof that the proxy carries traffic. The failure it exists for:
/// xray's SOCKS server answers "succeeded" before its outbound dials anything, so a probe
/// that trusted the reply accepted a dead Hysteria2 at connect and never noticed it after.
/// The fake below behaves the same way — success first, then whatever the outbound does.
/// </summary>
public class SocksRoundTripTests
{
    private enum Far { Answers, Closes, Silent, RefusesInReply }

    /// <summary>A SOCKS5 server on loopback that replies like xray's and then acts as told.</summary>
    private sealed class FakeSocks : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private readonly Func<string, Far> _far;
        private readonly byte _boundAddressType;

        public int Port { get; }
        public string? LastConnectHost { get; private set; }
        public int LastConnectPort { get; private set; }
        public string? LastRequest { get; private set; }

        public FakeSocks(Far far, byte boundAddressType = 0x01) : this(_ => far, boundAddressType) { }

        /// <summary>Behaves per CONNECT target, so one probe can find one host up and another down.</summary>
        public FakeSocks(Func<string, Far> far, byte boundAddressType = 0x01)
        {
            _far = far;
            _boundAddressType = boundAddressType;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var s = client.GetStream();
            try
            {
                await ReadAsync(s, 3);
                await s.WriteAsync(new byte[] { 5, 0 });

                var head = await ReadAsync(s, 5);
                var name = await ReadAsync(s, head[4]);
                var port = await ReadAsync(s, 2);
                LastConnectHost = Encoding.ASCII.GetString(name);
                LastConnectPort = port[0] << 8 | port[1];
                var far = _far(LastConnectHost);

                if (far == Far.RefusesInReply)
                {
                    await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                // Success at once, before anything is dialled — as xray does.
                var reply = _boundAddressType switch
                {
                    0x03 => new byte[] { 5, 0, 0, 3, 9 }.Concat("localhost"u8.ToArray()).Concat(new byte[] { 0, 0 }).ToArray(),
                    0x04 => new byte[] { 5, 0, 0, 4 }.Concat(new byte[16]).Concat(new byte[] { 0, 0 }).ToArray(),
                    _ => new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 },
                };
                await s.WriteAsync(reply);

                switch (far)
                {
                    case Far.Closes:
                        return;
                    case Far.Silent:
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    case Far.Answers:
                        var request = new StringBuilder();
                        var buf = new byte[1];
                        while (!request.ToString().EndsWith("\r\n\r\n"))
                        {
                            if (await s.ReadAsync(buf) == 0) return;
                            request.Append((char)buf[0]);
                        }
                        LastRequest = request.ToString();
                        await s.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                        return;
                }
            }
            catch { }
        }

        private static async Task<byte[]> ReadAsync(NetworkStream s, int n)
        {
            var b = new byte[n];
            var read = 0;
            while (read < n)
            {
                var got = await s.ReadAsync(b.AsMemory(read));
                if (got == 0) throw new IOException("closed");
                read += got;
            }
            return b;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
        }
    }

    private static readonly ProbeTarget Target = new("cp.cloudflare.com", 80, "/generate_204");
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_status_line_from_the_far_end_is_proof()
    {
        await using var socks = new FakeSocks(Far.Answers);
        Assert.True(await SocksRoundTrip.CarriesAsync(socks.Port, Target, Short, default));
        Assert.Equal("cp.cloudflare.com", socks.LastConnectHost);
        Assert.Equal(80, socks.LastConnectPort);
        Assert.StartsWith("GET /generate_204 HTTP/1.1\r\nHost: cp.cloudflare.com\r\n", socks.LastRequest);
    }

    [Fact]
    public async Task A_success_reply_followed_by_a_close_is_not()
    {
        // The dead-Hysteria2 case: the core said "succeeded", then the outbound failed.
        await using var socks = new FakeSocks(Far.Closes);
        Assert.False(await SocksRoundTrip.CarriesAsync(socks.Port, Target, Short, default));
    }

    [Fact]
    public async Task A_success_reply_followed_by_silence_is_not()
    {
        await using var socks = new FakeSocks(Far.Silent);
        var clock = Stopwatch.StartNew();
        Assert.False(await SocksRoundTrip.CarriesAsync(socks.Port, Target, TimeSpan.FromMilliseconds(500), default));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_refusal_in_the_reply_is_not()
    {
        await using var socks = new FakeSocks(Far.RefusesInReply);
        Assert.False(await SocksRoundTrip.CarriesAsync(socks.Port, Target, Short, default));
    }

    [Theory]
    [InlineData((byte)0x03)]
    [InlineData((byte)0x04)]
    public async Task The_bound_address_is_read_whatever_its_type(byte type)
    {
        await using var socks = new FakeSocks(Far.Answers, type);
        Assert.True(await SocksRoundTrip.CarriesAsync(socks.Port, Target, Short, default));
    }

    [Fact]
    public async Task Nothing_listening_is_not()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Assert.False(await SocksRoundTrip.CarriesAsync(port, Target, Short, default));
    }

    [Fact]
    public async Task One_answering_target_is_enough_and_a_hanging_one_is_not_waited_for()
    {
        await using var socks = new FakeSocks(host => host == "up.example" ? Far.Answers : Far.Silent);
        var clock = Stopwatch.StartNew();
        Assert.True(await SocksRoundTrip.AnyCarriesAsync(
            socks.Port, [new("down.example"), new("up.example")], TimeSpan.FromSeconds(20), default));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task No_target_answering_is_a_dead_proxy()
    {
        await using var socks = new FakeSocks(Far.Closes);
        Assert.False(await SocksRoundTrip.AnyCarriesAsync(socks.Port, SocksRoundTrip.DefaultTargets, Short, default));
    }

    [Fact]
    public void The_connect_request_names_the_host_and_port()
    {
        var r = SocksRoundTrip.ConnectRequest("1.1.1.1", 80);
        Assert.Equal(new byte[] { 5, 1, 0, 3, 7 }, r[..5]);
        Assert.Equal("1.1.1.1", Encoding.ASCII.GetString(r, 5, 7));
        Assert.Equal(new byte[] { 0, 80 }, r[^2..]);
    }
}
