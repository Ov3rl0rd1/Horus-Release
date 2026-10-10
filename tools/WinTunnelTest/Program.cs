// The Windows tunnel, end to end, on a real Windows machine.
//
// What it proves that nothing else can: the core's wintun adapter comes up inside this
// process; traffic through it reaches the proxy; the core's process rule sends a named
// executable direct while everything else goes through the proxy — which also proves the
// outbound binding, because with the default route in the TUN a direct socket that was not
// pinned to the physical interface would loop; DNS through the TUN's resolver answers both
// A and SRV; rules reload and outbounds swap without a restart; and the core survives being
// stopped and started under load (the wintun session teardown patched in the fork); a dead
// proxy fails the client's probe; and the adapter comes up when its usual identity is
// still held by another device (the fork's fork_open_windows.go).
//
// Run elevated: `dotnet run -c Release` from tools/WinTunnelTest. Exit code = failures.
// The "node" is a second copy of this program (`server` mode) hosting the core as a VLESS
// server on loopback, so the test needs nothing but the internet.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Horus.Protocols;

const int NodePort = 18443;
const int SocksPort = 10808;
const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";

if (args is ["server", ..])
{
    RunServer();
    return 0;
}

var failures = 0;
var dir = AppContext.BaseDirectory;
var logs = Path.Combine(dir, "logs");
Directory.CreateDirectory(logs);

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
    if (!ok) failures++;
}

Console.WriteLine($"core: {XrayInterop.Version()}  live controls: {XrayLive.IsSupported}");
using (var id = WindowsIdentity.GetCurrent())
    Check("running elevated", new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator));

// ── The node ─────────────────────────────────────────────────────────────────
var server = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "server")
{
    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir
})!;
await Task.Delay(1500);
Check("node started", !server.HasExited);

// ── The client, configured by the app's own code ─────────────────────────────
var outbound = JsonNode.Parse($$"""
    { "tag": "proxy", "protocol": "vless",
      "settings": { "vnext": [ { "address": "127.0.0.1", "port": {{NodePort}},
                    "users": [ { "id": "{{Uuid}}", "encryption": "none" } ] } ] },
      "streamSettings": { "network": "tcp" } }
    """)!;

var cfg = new XrayConfig
{
    Outbound = outbound,
    Offer = "wintest",
    ProtocolName = "vless",
    NodeAddress = "127.0.0.1",
    SocksPort = SocksPort,
    LogFilePath = Path.Combine(logs, "client.log"),
    LogLevel = "info"
};

string Run(WindowsSplitRules split) => WindowsTunnelConfig.Build(cfg.ToConfig(), new WindowsTunSettings(), split);

var blacklistCurl = new WindowsSplitRules(SplitTunnelingMode.Blacklist, ["curl.exe"]);
var runJson = Run(blacklistCurl);
File.WriteAllText(Path.Combine(logs, "client.json"), runJson);

try { XrayInterop.Test(runJson); Check("core accepts the Windows config", true); }
catch (Exception ex) { Check("core accepts the Windows config", false, ex.Message); }

var sw = Stopwatch.StartNew();
try { XrayInterop.Start(runJson); Check("core started with the TUN", true, $"{sw.ElapsedMilliseconds} ms"); }
catch (Exception ex) { Check("core started with the TUN", false, ex.Message); Finish(); return failures; }

Check("adapter up", await WaitAdapterAsync(TimeSpan.FromSeconds(15)), $"{sw.ElapsedMilliseconds} ms after start");
// md5 of the name, as upstream derives it: an installation keeps the adapter (and the network
// profile) it had before the fork learnt to pick another identity.
Check("the adapter has upstream's identity", AdapterGuid() == Identity(0), IdentityName(AdapterGuid()));
Console.WriteLine(Shell("powershell", "-NoProfile -Command \"Get-NetRoute -InterfaceAlias Horus -ErrorAction SilentlyContinue | Format-Table -AutoSize DestinationPrefix,RouteMetric,ifIndex | Out-String -Width 200\""));
Console.WriteLine(Shell("powershell", "-NoProfile -Command \"Get-DnsClientServerAddress -InterfaceAlias Horus | Format-Table -AutoSize | Out-String -Width 200\""));

var routes = RouteTable.DefaultRoutes(WindowsTunnelConfig.AdapterName);
var path = NetworkPathSelector.Select(routes);
Check("physical path found beside the TUN", path is not null, path?.Describe() ?? "none");

// ── Traffic through the proxy ────────────────────────────────────────────────
using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromSeconds(1) })
{
    Timeout = TimeSpan.FromSeconds(20)
};

async Task<(bool Ok, string Detail)> GetAsync(string url)
{
    try
    {
        var t = Stopwatch.StartNew();
        using var r = await http.GetAsync(url);
        return (r.IsSuccessStatusCode, $"{(int)r.StatusCode} in {t.ElapsedMilliseconds} ms");
    }
    catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message); }
}

var viaTun = await GetAsync("https://www.gstatic.com/generate_204");
Check("HTTPS through the TUN", viaTun.Ok, viaTun.Detail);

// ── Latency probes leave by the physical interface ───────────────────────────
// 192.0.2.1 (TEST-NET-1) never answers. Through the TUN the core's stack completes the
// handshake itself before dialling anything, so an unpinned probe "answers" at once — the
// ~1 ms every server showed in the list while connected. Pinned, it must time out.
var unpinned = await PhysicalProbe.ConnectAsync("192.0.2.1", 443, 0, TimeSpan.FromSeconds(3), false, default);
var pinned = await PhysicalProbe.ConnectAsync("192.0.2.1", 443, path?.InterfaceIndex ?? 0, TimeSpan.FromSeconds(3), false, default);
Check("an unpinned probe is answered by the TUN itself (what the pin is for)", unpinned is not null,
    unpinned is null ? "no answer" : $"{unpinned} ms");
Check("a probe pinned to the physical interface is not", path is not null && pinned is null,
    pinned is null ? "no answer, as it should" : $"answered in {pinned} ms");

var monitor = new WindowsConnectionMonitor();
var mine = Path.GetFileName(Environment.ProcessPath!);

// Keep a connection of ours open so the listing has something to show.
var keep = Task.Run(async () =>
{
    try { using var s = await http.GetStreamAsync("https://speed.cloudflare.com/__down?bytes=20000000"); var b = new byte[16384]; while (await s.ReadAsync(b) > 0) await Task.Delay(50); }
    catch { }
});
await Task.Delay(2500);

// ── The process rule: curl goes direct, we go through the proxy ──────────────
// -S keeps curl's own error text: a curl that fails (DNS, connect) must say why, not just
// be absent from the listing.
var curl = Process.Start(new ProcessStartInfo("curl.exe",
    "-sS -o NUL --limit-rate 40k https://speed.cloudflare.com/__down?bytes=2000000")
{ UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
var curlErr = curl.StandardError.ReadToEndAsync();

// Wait for curl's connection to show rather than for a fixed time: its first DNS lookup goes
// through the freshly started tunnel, and one run on a slow runner took longer than 3 s.
var curlClock = Stopwatch.StartNew();
IReadOnlyList<AppTraffic> apps;
AppTraffic? curlRow;
while (true)
{
    apps = monitor.Snapshot();
    curlRow = apps.FirstOrDefault(a => a.Name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase));
    if (curlRow is { Down: > 10_000 } || curl.HasExited || curlClock.Elapsed > TimeSpan.FromSeconds(15)) break;
    await Task.Delay(500);
}
foreach (var a in apps.Take(8))
    Console.WriteLine($"   {a.Name,-22} vpn {a.ViaVpn,3}  direct {a.Direct,3}  other {a.Other,3}  up {a.Up,10}  down {a.Down,10}  {string.Join(", ", a.Targets)}");

var curlState = curl.HasExited
    ? $"curl exited {curl.ExitCode}: {(await curlErr).Trim()}"
    : $"after {curlClock.ElapsedMilliseconds} ms";
var ourRow = apps.FirstOrDefault(a => a.Name.Equals(mine, StringComparison.OrdinalIgnoreCase));
Check("connections are attributed to processes", ourRow is not null && curlRow is not null,
    $"{apps.Count} app(s): {string.Join(", ", apps.Select(a => a.Name))}");
Check("curl.exe routed direct by the process rule", curlRow is { Direct: > 0, ViaVpn: 0 },
    curlRow is null ? $"not seen; {curlState}" : $"vpn {curlRow.ViaVpn}, direct {curlRow.Direct}, {curlState}");
Check("our own traffic routed through the proxy", ourRow is { ViaVpn: > 0, Direct: 0 },
    ourRow is null ? "not seen" : $"vpn {ourRow.ViaVpn}, direct {ourRow.Direct}");
Check("direct traffic flows (outbound binding holds)", curlRow is { Down: > 10_000 },
    curlRow is null ? curlState : $"{curlRow.Down} bytes back");

// ── DNS through the TUN's resolver ───────────────────────────────────────────
var a1 = Shell("powershell", $"-NoProfile -Command \"(Resolve-DnsName example.com -Type A -Server {WindowsTunnelConfig.DnsAddress} -DnsOnly -ErrorAction Stop).IPAddress\"");
Check("DNS A via the TUN resolver", IPAddress.TryParse(a1.Trim().Split('\n')[0].Trim(), out _), a1.Trim());
// Records that are known to exist; a blanked answer is an empty NOERROR, a forwarded one
// carries the target. Two, so one provider retiring a record does not fail the check.
var srv = Shell("powershell", $"-NoProfile -Command \"foreach ($n in '_imaps._tcp.gmail.com','_xmpp-client._tcp.jabber.org') {{ try {{ (Resolve-DnsName $n -Type SRV -Server {WindowsTunnelConfig.DnsAddress} -DnsOnly -ErrorAction Stop | Where-Object Type -eq 'SRV' | Select-Object -First 1).NameTarget }} catch {{ $_.Exception.Message }} }}\"");
Check("DNS SRV via the TUN resolver is forwarded, not blanked",
    srv.Contains("gmail.com", StringComparison.OrdinalIgnoreCase) || srv.Contains("jabber.org", StringComparison.OrdinalIgnoreCase),
    srv.Trim().Replace("\r\n", " | "));

// ── Live reload: whitelist us, so curl falls to the catch-all (direct) and we stay on the proxy ──
var whitelistUs = Run(new WindowsSplitRules(SplitTunnelingMode.Whitelist, [mine]));
var reload = XrayLive.ReloadRouting(WindowsTunnelConfig.RoutingOf(whitelistUs));
Check("routing reloads live", reload == XrayLive.Result.Ok, reload == XrayLive.Result.Ok ? "" : XrayLive.LastError);
var after = await GetAsync("https://www.gstatic.com/generate_204");
Check("traffic still flows after the reload", after.Ok, after.Detail);
try { curl.Kill(); } catch { }

// ── Restart our connections: they come back under the new rules ──────────────
var before = monitor.Snapshot().FirstOrDefault(a => a.Name.Equals(mine, StringComparison.OrdinalIgnoreCase));
if (before is not null)
{
    var closed = monitor.Restart(before);
    Check("restart closes an app's connections", closed > 0, $"{closed} closed");
}

// ── Swap the outbound in place ───────────────────────────────────────────────
var swap = XrayLive.ReplaceOutbound(WindowsTunnelConfig.OutboundOf(whitelistUs)!);
Check("outbound swaps in place", swap == XrayLive.Result.Ok, swap == XrayLive.Result.Ok ? "" : XrayLive.LastError);
var afterSwap = await GetAsync("https://www.gstatic.com/generate_204");
Check("traffic flows after the swap", afterSwap.Ok, afterSwap.Detail);
Check("adapter survived reload and swap", AdapterUp());

// ── A dead proxy is told apart from a live one ───────────────────────────────
// The client's probe goes through the core's SOCKS inbound, which the Windows config always
// routes to the proxy. xray answers a SOCKS CONNECT before its outbound has dialled anything,
// so only a request that comes back means something: the reply alone passed a dead Hysteria2
// in the field.
var live = await SocksRoundTrip.AnyCarriesAsync(SocksPort, SocksRoundTrip.DefaultTargets, TimeSpan.FromSeconds(10), default);
Check("a round trip through the live proxy answers", live);
var deadProxy = $$"""{ "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "127.0.0.1", "port": 1, "users": [ { "id": "{{Uuid}}", "encryption": "none" } ] } ] }, "streamSettings": { "network": "tcp" } }""";
var toDead = XrayLive.ReplaceOutbound(deadProxy);
var replyOnly = await SocksProbe.CanDialAsync(SocksPort, "cp.cloudflare.com", 80, TimeSpan.FromSeconds(5), default);
var deadAnswers = await SocksRoundTrip.AnyCarriesAsync(SocksPort, SocksRoundTrip.DefaultTargets, TimeSpan.FromSeconds(6), default);
Check("a dead proxy fails the round trip", toDead == XrayLive.Result.Ok && !deadAnswers,
    $"swap {toDead}; the SOCKS reply alone said {(replyOnly ? "succeeded" : "failed")}");
var back = XrayLive.ReplaceOutbound(WindowsTunnelConfig.OutboundOf(whitelistUs)!);
var afterBack = await GetAsync("https://www.gstatic.com/generate_204");
Check("the live proxy swaps back", back == XrayLive.Result.Ok && afterBack.Ok, afterBack.Detail);

// ── Stop and start under load ────────────────────────────────────────────────
var load = new CancellationTokenSource();
var loadTask = Task.Run(async () =>
{
    while (!load.IsCancellationRequested)
    {
        try { await http.GetAsync("https://www.gstatic.com/generate_204", load.Token); } catch { }
    }
});

var proc = Process.GetCurrentProcess();
var handlesBefore = proc.HandleCount;
var worstStop = 0L;
var cycleFailures = 0;
var identities = new List<string>();
for (var i = 1; i <= 12; i++)
{
    var t = Stopwatch.StartNew();
    XrayInterop.Stop();
    worstStop = Math.Max(worstStop, t.ElapsedMilliseconds);
    try { XrayInterop.Start(runJson); } catch (Exception ex) { cycleFailures++; Console.WriteLine($"   cycle {i}: start failed: {ex.Message}"); continue; }
    if (!await WaitAdapterAsync(TimeSpan.FromSeconds(15))) { cycleFailures++; Console.WriteLine($"   cycle {i}: adapter did not come back"); continue; }
    identities.Add(IdentityName(AdapterGuid()));
    var probe = await GetAsync("https://www.gstatic.com/generate_204");
    if (!probe.Ok) { cycleFailures++; Console.WriteLine($"   cycle {i}: {probe.Detail}"); }
}
load.Cancel();
await loadTask;
proc.Refresh();
Check("12 stop/start cycles under load", cycleFailures == 0, $"{cycleFailures} failed, slowest stop {worstStop} ms");
Check("stop never hangs", worstStop < 5000, $"{worstStop} ms");
Console.WriteLine($"   handles {handlesBefore} -> {proc.HandleCount}, private {proc.PrivateMemorySize64 >> 20} MB");
// A closed adapter's device is removed by the time the next one is created on a healthy
// machine, so the fork's fallback must not kick in here: every cycle on identity 0.
Check("restarts keep the same identity", identities.Count > 0 && identities.All(x => x == "identity 0"),
    string.Join(", ", identities.GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}")));

// ── An identity still held by a device is passed over ────────────────────────
// What a user hit: the previous adapter's device not yet gone, the same GUID asked for again,
// wintun waiting 15 s and failing with "problem code 0x1F" on every retry. A second adapter
// holding identity 0 stands in for the device that would not go.
XrayInterop.Stop();
var hold = Native.CreateWintun("HorusHold", Identity(0));
Check("a stand-in device holds identity 0", hold != IntPtr.Zero, hold == IntPtr.Zero ? $"error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}" : "");
var heldClock = Stopwatch.StartNew();
string? heldErr = null;
try { XrayInterop.Start(runJson); } catch (Exception ex) { heldErr = ex.Message; }
var heldUp = heldErr is null && await WaitAdapterAsync(TimeSpan.FromSeconds(20));
var heldIdentity = AdapterGuid();
Check("the TUN comes up beside it under another identity",
    heldUp && heldIdentity is not null && heldIdentity != Identity(0),
    $"{IdentityName(heldIdentity)} after {heldClock.ElapsedMilliseconds} ms{(heldErr is null ? "" : "; " + heldErr)}");
Check("without wintun's 15 s wait", heldUp && heldClock.Elapsed < TimeSpan.FromSeconds(10), $"{heldClock.ElapsedMilliseconds} ms");
var heldTraffic = await GetAsync("https://www.gstatic.com/generate_204");
Check("traffic flows on the other identity", heldTraffic.Ok, heldTraffic.Detail);
XrayInterop.Stop();
if (hold != IntPtr.Zero) Native.CloseWintun(hold);
string? freeErr = null;
try { XrayInterop.Start(runJson); } catch (Exception ex) { freeErr = ex.Message; }
var freeUp = freeErr is null && await WaitAdapterAsync(TimeSpan.FromSeconds(15));
Check("identity 0 is used again once it is free", freeUp && AdapterGuid() == Identity(0),
    freeErr ?? IdentityName(AdapterGuid()));

Finish();
return failures;

// ── helpers ──────────────────────────────────────────────────────────────────

void Finish()
{
    XrayInterop.Stop();
    try { if (!server.HasExited) { server.Kill(); server.WaitForExit(5000); } } catch { }
    foreach (var f in new[] { "client.log", "server.log" })
    {
        var p = Path.Combine(logs, f);
        if (!File.Exists(p)) continue;
        Console.WriteLine($"--- tail {f}");
        try
        {
            using var stream = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            foreach (var line in lines.TakeLast(25)) Console.WriteLine("   " + line.TrimEnd());
        }
        catch (Exception ex) { Console.WriteLine("   (unreadable: " + ex.Message + ")"); }
    }
    Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
}

// Live, through IP Helper: the managed adapter list is cached per process and would report
// the previous cycle's adapter as up.
static bool AdapterUp() => Native.AdapterUp(WindowsTunnelConfig.AdapterName);

// The fork's adapter identities (proxy/tun/fork_identity.go): md5 of the name, then of
// "name#i". new Guid(bytes) reads them in the layout windows.GUID has in memory.
static Guid Identity(int i) =>
    new(MD5.HashData(Encoding.UTF8.GetBytes(i == 0 ? WindowsTunnelConfig.AdapterName : $"{WindowsTunnelConfig.AdapterName}#{i}")));

static Guid? AdapterGuid() => Native.AdapterGuid(WindowsTunnelConfig.AdapterName);

static string IdentityName(Guid? guid)
{
    if (guid is not { } g) return "no adapter";
    for (var i = 0; i < 4; i++) if (Identity(i) == g) return $"identity {i}";
    return $"a GUID outside the set ({g})";
}

static async Task<bool> WaitAdapterAsync(TimeSpan timeout)
{
    var end = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < end)
    {
        if (AdapterUp()) return true;
        await Task.Delay(200);
    }
    return false;
}

static string Shell(string exe, string arguments)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return o;
    }
    catch (Exception ex) { return ex.Message; }
}

static void RunServer()
{
    var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(logDir);

    // The node shares the machine with the client, so its egress would follow the default
    // route into the client's TUN and loop. Pin it to the physical interface — read now,
    // before the TUN exists — the same way the client's core pins its own sockets.
    var physical = NetworkPathSelector.Select(RouteTable.DefaultRoutes(WindowsTunnelConfig.AdapterName))?.Name
        ?? throw new InvalidOperationException("no physical interface to pin the node to");
    var config = $$"""
        {
          "log": { "loglevel": "warning", "error": "{{Path.Combine(logDir, "server.log").Replace("\\", "/")}}" },
          "inbounds": [ { "listen": "127.0.0.1", "port": {{NodePort}}, "protocol": "vless",
                          "settings": { "clients": [ { "id": "{{Uuid}}" } ], "decryption": "none" },
                          "streamSettings": { "network": "tcp" } } ],
          "outbounds": [ { "protocol": "freedom",
                           "streamSettings": { "sockopt": { "interface": "{{physical}}" } } } ]
        }
        """;
    XrayInterop.Start(config);
    Thread.Sleep(Timeout.Infinite);
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern int ConvertInterfaceLuidToGuid(ref ulong luid, out Guid guid);

    [System.Runtime.InteropServices.DllImport("wintun.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WintunCreateAdapter(string name, string tunnelType, ref Guid requestedGuid);

    [System.Runtime.InteropServices.DllImport("wintun.dll")]
    private static extern void WintunCloseAdapter(IntPtr adapter);

    public static IntPtr CreateWintun(string name, Guid guid) => WintunCreateAdapter(name, "Horus test", ref guid);

    public static void CloseWintun(IntPtr adapter) => WintunCloseAdapter(adapter);

    public static Guid? AdapterGuid(string alias)
    {
        if (ConvertInterfaceAliasToLuid(alias, out var luid) != 0) return null;
        return ConvertInterfaceLuidToGuid(ref luid, out var guid) == 0 ? guid : null;
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ConvertInterfaceAliasToLuid(string alias, out ulong luid);

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern int GetIfEntry2(IntPtr row);

    public static bool AdapterUp(string alias)
    {
        if (ConvertInterfaceAliasToLuid(alias, out var luid) != 0) return false;
        var row = System.Runtime.InteropServices.Marshal.AllocHGlobal(1352);
        try
        {
            for (var i = 0; i < 1352; i += 8) System.Runtime.InteropServices.Marshal.WriteInt64(row, i, 0);
            System.Runtime.InteropServices.Marshal.WriteInt64(row, 0, (long)luid);
            return GetIfEntry2(row) == 0 && System.Runtime.InteropServices.Marshal.ReadInt32(row, 1156) == 1;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(row); }
    }
}
