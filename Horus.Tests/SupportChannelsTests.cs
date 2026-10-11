using Horus.Application;
using Xunit;

namespace Horus.Tests;

/// <summary>What appsettings.json says about support, as the buttons that open it.</summary>
public class SupportChannelsTests
{
    [Fact]
    public void The_shipped_settings_give_three_channels()
    {
        var list = SupportChannels.From("https://t.me/horusping", "https://vk.ru/horus_ping", "horusping@gmail.com");
        Assert.Equal([SupportChannels.Telegram, SupportChannels.Vk, SupportChannels.Email], list.Select(c => c.Name));
        Assert.Equal("https://t.me/horusping", list[0].Address.ToString());
        Assert.Equal("@horusping", list[0].Display);
        Assert.Equal("https://vk.ru/horus_ping", list[1].Address.ToString());
        Assert.Equal("vk.ru/horus_ping", list[1].Display);
        Assert.Equal("mailto:horusping@gmail.com", list[2].Address.ToString());
        Assert.Equal("horusping@gmail.com", list[2].Display);
    }

    [Theory]
    [InlineData("@horusping", "https://t.me/horusping")]
    [InlineData("t.me/horusping", "https://t.me/horusping")]
    [InlineData("  https://t.me/horusping/  ", "https://t.me/horusping/")]
    public void A_telegram_handle_or_bare_link_becomes_a_link(string written, string expected) =>
        Assert.Equal(expected, SupportChannels.From(written, null, null)[0].Address.ToString());

    [Fact]
    public void A_vk_handle_becomes_a_vk_link() =>
        Assert.Equal("https://vk.com/horus_ping", SupportChannels.From(null, "@horus_ping", null)[0].Address.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://t.me/")]          // a site, not a channel
    [InlineData("ftp://t.me/horusping")]   // not something a browser should be handed
    public void Nothing_to_open_is_left_out(string written) =>
        Assert.Empty(SupportChannels.From(written, null, null));

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("@gmail.com")]
    [InlineData("a b@c.d")]
    public void A_broken_email_is_left_out(string written) =>
        Assert.Empty(SupportChannels.From(null, null, written));

    [Fact]
    public void A_mailto_email_is_accepted_as_is() =>
        Assert.Equal("mailto:help@horus.example", SupportChannels.From(null, null, "mailto:help@horus.example")[0].Address.ToString());
}
