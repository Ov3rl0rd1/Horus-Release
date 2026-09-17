using System.Text.Json;
using System.Text.Json.Nodes;
using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The geo-routing contract with the rule files.
///
/// <para>Everything here fails silently in the field, which is why it is pinned. A renamed
/// category does not throw — xray refuses the config and the user sees "не удалось
/// подключиться" with no hint that routing data is the cause. An inverted rule order does
/// not throw either: the tunnel comes up, works, and sends the blocked half of the internet
/// out of the wrong end.</para>
/// </summary>
public class GeoRoutingTests
{
    /// <summary>
    /// The category names carried by <c>Ov3rl0rd1/horus-geoip</c> and
    /// <c>Ov3rl0rd1/horus-geosite</c>, taken from the <c>category</c> and
    /// <c>exclude_category</c> fields of their <c>config.json</c> files and confirmed
    /// against the built <c>.dat</c>s (which hold <c>RU</c>, and <c>RU</c> plus
    /// <c>RU-EXCLUDE</c>).
    ///
    /// <para>Changing either fork's config renames the category in the file, and this is
    /// the only place in the app that would notice.</para>
    /// </summary>
    [Fact]
    public void Category_names_match_the_shipped_rule_files()
    {
        Assert.Equal("geoip:ru", GeoCategories.DirectIps);
        Assert.Equal("geosite:ru", GeoCategories.DirectSites);
        Assert.Equal("geosite:ru-exclude", GeoCategories.ProxySites);
    }

    /// <summary>
    /// The geoip build carries only <c>RU</c>. Naming <c>geoip:private</c> — the habit from
    /// upstream v2fly files, and what most sample configs use for local ranges — would make
    /// the core fail to resolve it and refuse the config outright. The local ranges are
    /// written out as literal CIDRs instead, which they already were.
    /// </summary>
    [Fact]
    public void Nothing_names_a_category_the_files_do_not_carry()
    {
        var geo = GeoRoutingOptions.ForRussianBypass();

        var named = geo.DirectIps
            .Concat(geo.DirectSites)
            .Concat(geo.ProxyDomainExceptions)
            .Concat(geo.ProxyIpExceptions)
            .Where(v => v.StartsWith("geoip:", StringComparison.Ordinal)
                     || v.StartsWith("geosite:", StringComparison.Ordinal));

        string[] carried = [GeoCategories.DirectIps, GeoCategories.DirectSites, GeoCategories.ProxySites];

        Assert.All(named, v => Assert.Contains(v, carried));
    }

    /// <summary>
    /// <b>The rule that makes the feature safe rather than harmful.</b>
    ///
    /// <para>The <c>.dat</c> format cannot express "this suffix, except that domain", so
    /// <c>geosite:ru</c> carries a rule for the whole <c>.ru</c> zone and a blocked <c>.ru</c>
    /// publication matches it no matter what the geosite build removes from its lists. The
    /// only thing that keeps Meduza and the rest inside the tunnel is <c>ru-exclude</c>
    /// being matched first. Invert this and the app routes exactly the traffic the user
    /// installed it for straight out of their own ISP — while reporting itself connected.</para>
    /// </summary>
    [Fact]
    public void The_blocked_set_is_matched_before_the_russian_set()
    {
        var geo = GeoRoutingOptions.ForRussianBypass();
        var rules = RoutingRules(geo);

        var exclude = IndexOfDomainRule(rules, GeoCategories.ProxySites);
        var direct = IndexOfDomainRule(rules, GeoCategories.DirectSites);

        Assert.True(exclude >= 0, "ru-exclude must be emitted at all");
        Assert.True(direct >= 0, "ru must be emitted at all");
        Assert.True(exclude < direct, "ru-exclude must come first or it can never win");

        Assert.Equal(XrayConfigBuilder.ProxyTag, rules[exclude].GetProperty("outboundTag").GetString());
        Assert.Equal(XrayConfigBuilder.DirectTag, rules[direct].GetProperty("outboundTag").GetString());
    }

    /// <summary>
    /// A user's own exception must also beat the category, so it is emitted alongside
    /// <c>ru-exclude</c> rather than after the direct rules.
    /// </summary>
    [Fact]
    public void A_user_exception_also_beats_the_russian_set()
    {
        var geo = GeoRoutingOptions.ForRussianBypass(userProxyDomains: ["example.ru"]);
        var rules = RoutingRules(geo);

        var user = IndexOfDomainRule(rules, "example.ru");
        var direct = IndexOfDomainRule(rules, GeoCategories.DirectSites);

        Assert.True(user >= 0 && direct >= 0);
        Assert.True(user < direct);
        Assert.Contains(GeoCategories.ProxySites,
            rules[IndexOfDomainRule(rules, GeoCategories.ProxySites)]
                .GetProperty("domain").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>
    /// The default is off, and it has to be: <see cref="GeoRoutingOptions.Disabled"/> is what
    /// the connect path falls back to whenever the files turn out to be unusable, and if it
    /// emitted anything the fallback would be the failure it exists to avoid.
    /// </summary>
    [Fact]
    public void The_disabled_options_emit_no_geo_rules()
    {
        Assert.False(GeoRoutingOptions.Disabled.HasAnything);

        foreach (var rule in RoutingRules(GeoRoutingOptions.Disabled))
        {
            Assert.False(rule.TryGetProperty("domain", out _));

            if (rule.TryGetProperty("ip", out var ip))
                Assert.DoesNotContain(ip.EnumerateArray().Select(v => v.GetString()),
                    v => v is not null && v.StartsWith("geoip:", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// <b>What keeps Windows from eating itself.</b>
    ///
    /// <para>There, <c>direct</c> is the OS route table and the tunnel holds the default
    /// route, so an unpinned direct rule sends the packet back into the TUN, through hev,
    /// into this core's own SOCKS5 inbound, and out <c>direct</c> again — forever. The pin
    /// becomes <c>IP_UNICAST_IF</c>, which overrides the route lookup for that socket. If
    /// this stops being emitted, the tunnel still comes up and still reports itself healthy.</para>
    /// </summary>
    [Fact]
    public void The_direct_outbound_can_be_pinned_to_an_interface()
    {
        var direct = DirectOutbound(Build(cfg =>
        {
            cfg.Geo = GeoRoutingOptions.ForRussianBypass();
            cfg.DirectInterface = "Ethernet";
        }));

        Assert.Equal("Ethernet", direct
            .GetProperty("streamSettings")
            .GetProperty("sockopt")
            .GetProperty("interface").GetString());
    }

    /// <summary>
    /// Nothing is emitted where nothing is needed. On Android the UID exclusion has already
    /// taken the core's sockets off the tunnel, and naming an interface there would only add
    /// a way to name the wrong one.
    /// </summary>
    [Fact]
    public void No_sockopt_is_emitted_when_no_interface_is_named()
    {
        var direct = DirectOutbound(Build(cfg => cfg.Geo = GeoRoutingOptions.ForRussianBypass()));

        Assert.False(direct.TryGetProperty("streamSettings", out _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static JsonElement Build(Action<XrayConfig>? tweak = null)
    {
        var config = new XrayConfig
        {
            Outbound = JsonNode.Parse("""{"protocol":"vless","settings":{}}""")!,
            Offer = "test"
        };

        tweak?.Invoke(config);

        using var doc = JsonDocument.Parse(XrayConfigBuilder.Build(config));
        return doc.RootElement.Clone();
    }

    private static JsonElement DirectOutbound(JsonElement root) =>
        root.GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("tag").GetString() == XrayConfigBuilder.DirectTag);

    private static List<JsonElement> RoutingRules(GeoRoutingOptions geo)
    {
        var root = Build(cfg => cfg.Geo = geo);

        return [.. root
            .GetProperty("routing").GetProperty("rules")
            .EnumerateArray()
            .Select(e => e.Clone())];
    }

    private static int IndexOfDomainRule(List<JsonElement> rules, string value) =>
        rules.FindIndex(r => r.TryGetProperty("domain", out var d)
            && d.EnumerateArray().Any(v => v.GetString() == value));
}
