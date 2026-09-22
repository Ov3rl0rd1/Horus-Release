using Horus.Application.Routing;
using Horus.Domain.Models;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// User-entered site rules, on their way into the core's routing section.
///
/// <para>The stakes are why these are thorough. xray does not skip a routing rule it cannot
/// parse — it rejects the whole config, XrayStart fails, and the tunnel does not come up. So
/// anything that reaches the config has to be something the core will accept, and everything
/// else has to be dropped here. A user who types a URL into a box and can then no longer
/// connect at all has been handed a way to break the app with a typo.</para>
/// </summary>
public class SiteRulesTests
{
    [Theory]
    [InlineData("youtube.com", "domain:youtube.com")]
    [InlineData("  youtube.com  ", "domain:youtube.com")]
    [InlineData("YouTube.COM", "domain:youtube.com")]
    [InlineData(".youtube.com", "domain:youtube.com")]
    [InlineData("youtube.com.", "domain:youtube.com")]
    public void A_bare_name_becomes_a_domain_match(string input, string expected) =>
        Assert.Equal(expected, SiteRules.Normalise(input));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=x", "domain:www.youtube.com")]
    [InlineData("http://example.org", "domain:example.org")]
    [InlineData("example.org:8443", "domain:example.org")]
    [InlineData("user:pw@example.org", "domain:example.org")]
    [InlineData("example.org/path#frag", "domain:example.org")]
    public void A_pasted_url_is_reduced_to_its_host(string input, string expected) =>
        Assert.Equal(expected, SiteRules.Normalise(input));

    [Theory]
    [InlineData("domain:youtube.com")]
    [InlineData("full:www.youtube.com")]
    [InlineData("keyword:googlevideo")]
    [InlineData("geosite:category-ads")]
    public void An_explicit_prefix_is_left_alone(string input) =>
        Assert.Equal(input, SiteRules.Normalise(input));

    [Fact]
    public void A_regexp_survives_untouched()
    {
        // Everything the URL handling does — splitting on ':', '/', '?', '#' — would corrupt
        // a regular expression into something that either matches nothing or will not parse.
        const string pattern = @"regexp:^.+\.googlevideo\.com$";
        Assert.Equal(pattern, SiteRules.Normalise(pattern));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]          // no dot: not a routable name
    [InlineData("мой банк")]           // free text someone typed into the box
    [InlineData("example..com")]
    [InlineData("8.8.8.8")]            // an address is an IP rule, not a domain one
    [InlineData("192.168.0.1")]
    [InlineData("domain:")]            // a prefix with nothing after it
    public void Anything_the_core_would_refuse_is_dropped(string? input) =>
        Assert.Null(SiteRules.Normalise(input));

    [Fact]
    public void An_internationalised_name_is_kept()
    {
        // The core takes UTF-8 domains; rejecting them would quietly exclude every user who
        // wanted to route a Cyrillic domain.
        Assert.Equal("domain:пример.рф", SiteRules.Normalise("Пример.РФ"));
    }

    [Fact]
    public void Parsing_drops_the_unusable_and_the_repeated()
    {
        var rules = SiteRules.Parse([
            ("youtube.com", RuleAction.Proxy),
            ("not a domain", RuleAction.Proxy),
            ("YOUTUBE.com", RuleAction.Direct),     // same pattern once normalised
            ("example.org", RuleAction.Direct),
        ]);

        Assert.Equal(2, rules.Count);

        // First mention wins: re-adding a site must not silently flip the earlier rule.
        Assert.Equal(RuleAction.Proxy, rules[0].Action);
        Assert.Equal("domain:youtube.com", rules[0].Pattern);
    }

    [Fact]
    public void Grouping_keeps_order_within_a_destination()
    {
        var rules = SiteRules.Parse([
            ("a.com", RuleAction.Direct),
            ("b.com", RuleAction.Proxy),
            ("c.com", RuleAction.Direct),
        ]);

        Assert.Equal(["domain:a.com", "domain:c.com"], SiteRules.For(rules, RuleAction.Direct));
        Assert.Equal(["domain:b.com"], SiteRules.For(rules, RuleAction.Proxy));
        Assert.Empty(SiteRules.For(rules, RuleAction.Reject));
    }
}
