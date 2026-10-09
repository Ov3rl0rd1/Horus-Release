using System.Text.Json.Nodes;
using Horus.Domain.Models;
using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// Writes the exact config the Windows client would hand its core, for a real core to run.
///
/// <para>The unit tests above pin the shape; only the core can say whether it accepts it and
/// whether traffic actually flows. Set <c>HORUS_BENCH_OUTBOUND</c> to a file holding a node
/// outbound and <c>HORUS_BENCH_OUT</c> to a directory, run this test, and feed the files to
/// <c>xray run -test -c</c> or to the namespace bench (see the <c>tunnel-bench</c> skill).
/// Skipped otherwise.</para>
/// </summary>
public class WindowsTunnelBenchExport
{
    [BenchExportFact]
    public void Export_windows_configs_for_the_bench()
    {
        var outbound = JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("HORUS_BENCH_OUTBOUND")!))!;
        var dir = Environment.GetEnvironmentVariable("HORUS_BENCH_OUT")!;
        Directory.CreateDirectory(dir);

        var cfg = new XrayConfig
        {
            Outbound = outbound,
            Offer = "bench",
            ProtocolName = outbound["protocol"]?.GetValue<string>() ?? "unknown",
            LogFilePath = Path.Combine(dir, "xray.log"),
            LogLevel = "warning",
            // The bench's targets live in 10/8; the app treats that as LAN. Everything else is
            // the app's own envelope, untouched.
            DnsServers = ["10.99.1.1"]
        };

        var baseJson = cfg.ToConfig();
        File.WriteAllText(Path.Combine(dir, "base.json"), baseJson);

        var tun = new WindowsTunSettings(Name: Environment.GetEnvironmentVariable("HORUS_BENCH_TUN") ?? "xtun0", CaptureIpv6: false);
        File.WriteAllText(Path.Combine(dir, "windows.json"),
            WindowsTunnelConfig.Build(baseJson, tun, WindowsSplitRules.None));
        File.WriteAllText(Path.Combine(dir, "windows-blacklist.json"),
            WindowsTunnelConfig.Build(baseJson, tun, new WindowsSplitRules(SplitTunnelingMode.Blacklist, ["curl"])));
        File.WriteAllText(Path.Combine(dir, "windows-whitelist.json"),
            WindowsTunnelConfig.Build(baseJson, tun, new WindowsSplitRules(SplitTunnelingMode.Whitelist, ["load"])));
    }
}

public sealed class BenchExportFactAttribute : FactAttribute
{
    public override string? Skip
    {
        get => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HORUS_BENCH_OUT"))
               || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HORUS_BENCH_OUTBOUND"))
            ? "HORUS_BENCH_OUT / HORUS_BENCH_OUTBOUND not set — nothing exported"
            : base.Skip;
        set => base.Skip = value;
    }
}
