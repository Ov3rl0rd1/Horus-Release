// The Windows tunnel, end to end, on a real Windows machine.
//
// What it proves that nothing else can: the core's wintun adapter comes up inside this
// process; traffic through it reaches the proxy; the core's process rule sends a named
// executable direct while everything else goes through the proxy — which also proves the
// outbound binding, because with the default route in the TUN a direct socket that was not
// pinned to the physical interface would loop; DNS through the TUN's resolver answers both
// A and SRV; rules reload and outbounds swap without a restart; and the core survives being
// stopped and started under load (the wintun session teardown patched in the fork).
//
// Run elevated: `dotnet run -c Release` from tools/WinTunnelTest. Exit code = failures.
// The "node" is a second copy of this program (`server` mode) hosting the core as a VLESS
// server on loopback, so the test needs nothing but the internet.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
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
var curl = Process.Start(new ProcessStartInfo("curl.exe",
    "-s -o NUL --limit-rate 40k https://speed.cloudflare.com/__down?bytes=2000000")
{ UseShellExecute = false, CreateNoWindow = true })!;
await Task.Delay(3000);

var apps = monitor.Snapshot();
foreach (var a in apps.Take(8))
    Console.WriteLine($"   {a.Name,-22} vpn {a.ViaVpn,3}  direct {a.Direct,3}  other {a.Other,3}  up {a.Up,10}  down {a.Down,10}  {string.Join(", ", a.Targets)}");

var curlRow = apps.FirstOrDefault(a => a.Name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase));
var ourRow = apps.FirstOrDefault(a => a.Name.Equals(mine, StringComparison.OrdinalIgnoreCase));
Check("connections are attributed to processes", ourRow is not null && curlRow is not null,
    $"{apps.Count} app(s): {string.Join(", ", apps.Select(a => a.Name))}");
Check("curl.exe routed direct by the process rule", curlRow is { Direct: > 0, ViaVpn: 0 },
    curlRow is null ? "not seen" : $"vpn {curlRow.ViaVpn}, direct {curlRow.Direct}");
Check("our own traffic routed through the proxy", ourRow is { ViaVpn: > 0, Direct: 0 },
    ourRow is null ? "not seen" : $"vpn {ourRow.ViaVpn}, direct {ourRow.Direct}");
Check("direct traffic flows (outbound binding holds)", curlRow is { Down: > 10_000 }, curlRow is null ? "" : $"{curlRow.Down} bytes back");

// ── DNS through the TUN's resolver ───────────────────────────────────────────
var a1 = Shell("powershell", $"-NoProfile -Command \"(Resolve-DnsName example.com -Type A -Server {WindowsTunnelConfig.DnsAddress} -DnsOnly -ErrorAction Stop).IPAddress\"");
Check("DNS A via the TUN resolver", IPAddress.TryParse(a1.Trim().Split('\n')[0].Trim(), out _), a1.Trim());
var srv = Shell("powershell", $"-NoProfile -Command \"(Resolve-DnsName _xmpp-server._tcp.gmail.com -Type SRV -Server {WindowsTunnelConfig.DnsAddress} -DnsOnly -ErrorAction Stop | Where-Object Type -eq 'SRV' | Select-Object -First 1).NameTarget\"");
Check("DNS SRV via the TUN resolver is forwarded, not blanked", srv.Contains("google", StringComparison.OrdinalIgnoreCase), srv.Trim());

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
for (var i = 1; i <= 12; i++)
{
    var t = Stopwatch.StartNew();
    XrayInterop.Stop();
    worstStop = Math.Max(worstStop, t.ElapsedMilliseconds);
    try { XrayInterop.Start(runJson); } catch (Exception ex) { cycleFailures++; Console.WriteLine($"   cycle {i}: start failed: {ex.Message}"); continue; }
    if (!await WaitAdapterAsync(TimeSpan.FromSeconds(15))) { cycleFailures++; Console.WriteLine($"   cycle {i}: adapter did not come back"); continue; }
    var probe = await GetAsync("https://www.gstatic.com/generate_204");
    if (!probe.Ok) { cycleFailures++; Console.WriteLine($"   cycle {i}: {probe.Detail}"); }
}
load.Cancel();
await loadTask;
proc.Refresh();
Check("12 stop/start cycles under load", cycleFailures == 0, $"{cycleFailures} failed, slowest stop {worstStop} ms");
Check("stop never hangs", worstStop < 5000, $"{worstStop} ms");
Console.WriteLine($"   handles {handlesBefore} -> {proc.HandleCount}, private {proc.PrivateMemorySize64 >> 20} MB");

Finish();
return failures;

// ── helpers ──────────────────────────────────────────────────────────────────

void Finish()
{
    XrayInterop.Stop();
    try { if (!server.HasExited) server.Kill(); } catch { }
    foreach (var f in new[] { "client.log", "server.log" })
    {
        var p = Path.Combine(logs, f);
        if (!File.Exists(p)) continue;
        Console.WriteLine($"--- tail {f}");
        foreach (var line in File.ReadLines(p).TakeLast(25)) Console.WriteLine("   " + line);
    }
    Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
}

// Live, through IP Helper: the managed adapter list is cached per process and would report
// the previous cycle's adapter as up.
static bool AdapterUp() => Native.AdapterUp(WindowsTunnelConfig.AdapterName);

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
