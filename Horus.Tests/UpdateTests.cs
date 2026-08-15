using Horus.Application.Update;
using Horus.Domain.Models;
using Xunit;

namespace Horus.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v0.1.0", 0, 1, 0)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData(" 0.9.0 ", 0, 9, 0)]
    [InlineData("1.2", 1, 2, 0)]                 // a two-part tag reads as x.y.0
    [InlineData("0.9.0+3f2a91c", 0, 9, 0)]       // AssemblyInformationalVersion carries a commit
    public void Parses(string text, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(major, minor, patch), v);
    }

    [Fact]
    public void KeepsThePreReleaseLabel()
    {
        Assert.True(AppVersion.TryParse("v1.0.0-rc.2", out var v));
        Assert.Equal(1, v.Major);
        Assert.Equal("rc.2", v.PreRelease);
        Assert.True(v.IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("v.1.2")]
    public void RejectsWhatIsNotAVersion(string? text) =>
        Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Orders()
    {
        Assert.True(AppVersion.Parse("0.2.0") > AppVersion.Parse("0.1.9"));
        Assert.True(AppVersion.Parse("1.0.0") > AppVersion.Parse("0.99.99"));
        Assert.True(AppVersion.Parse("0.1.10") > AppVersion.Parse("0.1.9"));
        Assert.True(AppVersion.Parse("0.1.0") < AppVersion.Parse("0.1.1"));
    }

    [Fact]
    public void APreReleaseIsBelowItsRelease()
    {
        // The published 1.0.0 has to supersede a tester's 1.0.0-rc1, or testers get stuck
        // on release candidates forever.
        Assert.True(AppVersion.Parse("1.0.0") > AppVersion.Parse("1.0.0-rc1"));
        Assert.True(AppVersion.Parse("1.0.0-rc2") > AppVersion.Parse("1.0.0-rc1"));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.1", UpdateUrgency.Deferred)]   // third digit: can wait
    [InlineData("0.1.0", "0.1.9", UpdateUrgency.Deferred)]
    [InlineData("0.1.0", "0.2.0", UpdateUrgency.Immediate)]  // second digit: soon
    [InlineData("0.1.0", "1.0.0", UpdateUrgency.Immediate)]  // first digit: soon
    [InlineData("0.1.0", "0.1.0", UpdateUrgency.None)]
    [InlineData("0.2.0", "0.1.0", UpdateUrgency.None)]       // never downgrade
    public void UrgencySplitsOnTheFirstTwoComponents(string current, string candidate, UpdateUrgency expected) =>
        Assert.Equal(expected, AppVersion.Parse(current).UrgencyOf(AppVersion.Parse(candidate)));

    [Fact]
    public void RoundTripsThroughItsOwnText()
    {
        // The pending version is persisted as a string and parsed back on the next launch.
        foreach (var text in new[] { "0.1.0", "1.2.3", "2.0.0-rc.1" })
            Assert.Equal(text, AppVersion.Parse(text).ToString());
    }
}

public class Sha256SumsTests
{
    /// <summary>The real file from a 0.1.0 release, verbatim.</summary>
    private const string Sample = """
        f476f18593ddf0c048c50e0d1fd6561ac89db36ac88122ea27938ce410807f69  CERTIFICATE-THUMBPRINT.txt
        ca6f077f74f18ea7c8112a3a3c7d797670a2b94d1e47d658e66043bcf8db6ef9  Horus-0.1.0-android-arm64-v8a.apk
        368193f1368934e6d0b1fca76eb7c93c802f42957ec4ca4730a86d53d0bffb0a  Horus-0.1.0-android-x86_64.apk
        f664b691bc96ed86b545e45c7c7c3b57f834233f2491eb73f69e07820788f2ef  Horus-0.1.0-win-x64-portable.zip
        cbc3541f355273e6833e443a7442c7d1ab98dbad0f7d0ec5b2a9bd18e308cf95  Horus-0.1.0-win-x64.msi
        feb923a851b31f2b1d3dfca7f7ebe657871593c2aa1c3410d57c60ae6b419a0a  Horus-0.1.0-win-x64.wixpdb
        """;

    [Fact]
    public void ParsesEveryEntry()
    {
        var sums = Sha256Sums.Parse(Sample);
        Assert.Equal(6, sums.Count);
        Assert.Equal("cbc3541f355273e6833e443a7442c7d1ab98dbad0f7d0ec5b2a9bd18e308cf95",
            sums["Horus-0.1.0-win-x64.msi"]);
    }

    [Fact]
    public void ReadsTheVersionOutOfTheArtifactNames()
    {
        // With no version in the site's download URLs, this file is the only place the
        // fallback source can learn what the latest release is.
        var sums = Sha256Sums.Parse(Sample);
        Assert.Equal(new AppVersion(0, 1, 0), Sha256Sums.InferVersion(sums.Keys));
    }

    [Fact]
    public void IgnoresFilesThatCarryNoVersion() =>
        Assert.Equal(AppVersion.Zero,
            Sha256Sums.InferVersion(["CERTIFICATE-THUMBPRINT.txt", "SHA256SUMS.txt", "readme.md"]));

    [Fact]
    public void TheVersionStopsAtThePlatformToken()
    {
        // The trap: "0.1.0-win" parses as a valid pre-release that sorts BELOW 0.1.0, so a
        // greedy match would make every release look older than the installed build and the
        // updater would silently never fire.
        Assert.Equal(new AppVersion(0, 1, 0),
            Sha256Sums.InferVersion(["Horus-0.1.0-win-x64.msi"]));
        Assert.Equal(new AppVersion(0, 1, 0),
            Sha256Sums.InferVersion(["Horus-0.1.0-android-arm64-v8a.apk"]));
    }

    [Fact]
    public void APreReleaseArtifactKeepsItsLabel()
    {
        var v = Sha256Sums.InferVersion(["Horus-0.2.0-rc1-win-x64.msi"]);
        Assert.Equal(new AppVersion(0, 2, 0, "rc1"), v);
        Assert.True(v < new AppVersion(0, 2, 0));
    }

    [Fact]
    public void TakesTheHighestWhenNamesDisagree() =>
        Assert.Equal(new AppVersion(0, 2, 0), Sha256Sums.InferVersion(
            ["Horus-0.1.0-win-x64.msi", "Horus-0.2.0-win-x64.msi"]));

    [Theory]
    [InlineData("-win-x64.msi", "cbc3541f355273e6833e443a7442c7d1ab98dbad0f7d0ec5b2a9bd18e308cf95")]
    [InlineData("-win-x64-portable.zip", "f664b691bc96ed86b545e45c7c7c3b57f834233f2491eb73f69e07820788f2ef")]
    [InlineData("-android-arm64-v8a.apk", "ca6f077f74f18ea7c8112a3a3c7d797670a2b94d1e47d658e66043bcf8db6ef9")]
    [InlineData("-android-x86_64.apk", "368193f1368934e6d0b1fca76eb7c93c802f42957ec4ca4730a86d53d0bffb0a")]
    public void MatchesAssetsBySuffix(string suffix, string expected)
    {
        // The suffix is how one checksum list serves both the versioned GitHub names and
        // the unversioned ones the site publishes.
        var sums = Sha256Sums.Parse(Sample);
        Assert.Equal(expected, Sha256Sums.DigestForSuffix(sums, suffix));
    }

    [Fact]
    public void TheMsiSuffixDoesNotMatchThePortableZip()
    {
        // ".msi" and "-portable.zip" both end in "x64…"; picking the wrong one downloads a
        // real file that then fails its checksum, which reads as a corrupt download.
        var sums = Sha256Sums.Parse(Sample);
        Assert.NotEqual(
            Sha256Sums.DigestForSuffix(sums, "-win-x64.msi"),
            Sha256Sums.DigestForSuffix(sums, "-win-x64-portable.zip"));
    }

    [Fact]
    public void SkipsMalformedLinesInsteadOfFailingTheFile()
    {
        var sums = Sha256Sums.Parse("""
            # a comment
            not-a-hash  Horus-0.1.0-win-x64.msi
            abc  short
            cbc3541f355273e6833e443a7442c7d1ab98dbad0f7d0ec5b2a9bd18e308cf95  Horus-0.1.0-win-x64.msi

            """);
        Assert.Single(sums);
    }

    [Fact]
    public void AcceptsBinaryModeMarkers()
    {
        var sums = Sha256Sums.Parse(
            "cbc3541f355273e6833e443a7442c7d1ab98dbad0f7d0ec5b2a9bd18e308cf95 *Horus-0.1.0-win-x64.msi");
        Assert.Equal("Horus-0.1.0-win-x64.msi", Assert.Single(sums).Key);
    }

    [Fact]
    public void HandlesNothing()
    {
        Assert.Empty(Sha256Sums.Parse(null));
        Assert.Empty(Sha256Sums.Parse(""));
    }
}

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static UpdatePlan Plan(UpdateUrgency urgency, TimeSpan? age = null) => new(
        new UpdateManifest(new AppVersion(0, 2, 0), "v0.2.0", UpdateOrigin.GitHub, []),
        new UpdateAsset("Horus-0.2.0-win-x64.msi", "https://example/x", new string('a', 64)),
        urgency,
        Now - (age ?? TimeSpan.Zero));

    private static DeviceConditions Device(
        bool network = true, bool unmetered = true, bool charging = true, bool interactive = false) =>
        new(network, unmetered, charging, interactive);

    // ── Download ────────────────────────────────────────────────────────

    [Fact]
    public void NothingDownloadsWithoutANetwork() =>
        Assert.Equal(UpdateHold.NoNetwork,
            UpdatePolicy.CanDownload(Plan(UpdateUrgency.Immediate), Device(network: false), Now));

    [Fact]
    public void ASmallUpdateWaitsForWifi() =>
        Assert.Equal(UpdateHold.WaitingForWifi,
            UpdatePolicy.CanDownload(Plan(UpdateUrgency.Deferred), Device(unmetered: false), Now));

    [Fact]
    public void ASmallUpdateStillWaitsForWifiAfterDays() =>
        // The choice was mobile data for big updates only; a small one waits indefinitely.
        Assert.Equal(UpdateHold.WaitingForWifi,
            UpdatePolicy.CanDownload(
                Plan(UpdateUrgency.Deferred, age: TimeSpan.FromDays(3)), Device(unmetered: false), Now));

    [Fact]
    public void ASmallUpdateDownloadsOnWifi() =>
        Assert.Equal(UpdateHold.None,
            UpdatePolicy.CanDownload(Plan(UpdateUrgency.Deferred), Device(), Now));

    [Fact]
    public void ABigUpdatePrefersWifiAtFirst() =>
        Assert.Equal(UpdateHold.WaitingForWifi,
            UpdatePolicy.CanDownload(
                Plan(UpdateUrgency.Immediate, age: TimeSpan.FromMinutes(30)), Device(unmetered: false), Now));

    [Fact]
    public void ABigUpdateAcceptsMobileDataAfterTheGrace() =>
        Assert.Equal(UpdateHold.None,
            UpdatePolicy.CanDownload(
                Plan(UpdateUrgency.Immediate, age: TimeSpan.FromHours(3)), Device(unmetered: false), Now));

    // ── Install ─────────────────────────────────────────────────────────

    [Fact]
    public void ABigUpdateInstallsEvenOverALiveTunnel() =>
        Assert.Equal(UpdateHold.None,
            UpdatePolicy.CanInstall(Plan(UpdateUrgency.Immediate), Device(), vpnConnected: true, Now, 14));

    [Fact]
    public void ASmallUpdateWaitsForTheTunnelToGoDown() =>
        Assert.Equal(UpdateHold.WaitingForVpnOff,
            UpdatePolicy.CanInstall(Plan(UpdateUrgency.Deferred), Device(), vpnConnected: true, Now, 3));

    [Fact]
    public void ASmallUpdateWaitsForNight() =>
        Assert.Equal(UpdateHold.WaitingForQuietHours,
            UpdatePolicy.CanInstall(Plan(UpdateUrgency.Deferred), Device(), vpnConnected: false, Now, 14));

    [Fact]
    public void ASmallUpdateWaitsForCharge() =>
        Assert.Equal(UpdateHold.WaitingForQuietHours,
            UpdatePolicy.CanInstall(
                Plan(UpdateUrgency.Deferred), Device(charging: false), vpnConnected: false, Now, 3));

    [Fact]
    public void ASmallUpdateWaitsForTheScreenToGoOff() =>
        Assert.Equal(UpdateHold.WaitingForQuietHours,
            UpdatePolicy.CanInstall(
                Plan(UpdateUrgency.Deferred), Device(interactive: true), vpnConnected: false, Now, 3));

    [Fact]
    public void ASmallUpdateGoesInAtNightOnChargeWithTheScreenOff() =>
        Assert.Equal(UpdateHold.None,
            UpdatePolicy.CanInstall(Plan(UpdateUrgency.Deferred), Device(), vpnConnected: false, Now, 3));

    [Fact]
    public void TheDeadlineOverridesEverythingIncludingALiveTunnel()
    {
        // A user who never turns the VPN off would otherwise never update, and that is the
        // usage this product is designed around.
        var overdue = Plan(UpdateUrgency.Deferred, age: TimeSpan.FromHours(25));
        Assert.Equal(UpdateHold.None,
            UpdatePolicy.CanInstall(overdue, Device(charging: false, interactive: true),
                vpnConnected: true, Now, 14));
    }

    [Fact]
    public void TheDeadlineIsTwentyFourHours()
    {
        var almost = Plan(UpdateUrgency.Deferred, age: TimeSpan.FromHours(23.5));
        Assert.NotEqual(UpdateHold.None,
            UpdatePolicy.CanInstall(almost, Device(interactive: true), vpnConnected: true, Now, 14));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(23, false)]
    public void QuietHoursAreOneToSix(int hour, bool quiet) =>
        Assert.Equal(quiet, UpdatePolicy.IsQuietHour(hour));

    [Fact]
    public void PollingSlowsDownWhenThereIsNothingToDo()
    {
        Assert.True(UpdatePolicy.NextPoll(null) > UpdatePolicy.NextPoll(Plan(UpdateUrgency.Deferred)));
        Assert.True(UpdatePolicy.NextPoll(Plan(UpdateUrgency.Deferred))
                  > UpdatePolicy.NextPoll(Plan(UpdateUrgency.Immediate)));
    }
}

public class GitHubApiTests
{
    [Theory]
    [InlineData("https://github.com/Ov3rl0rd1/Horus-Release/releases")]
    [InlineData("https://github.com/Ov3rl0rd1/Horus-Release/releases/")]
    [InlineData("https://github.com/Ov3rl0rd1/Horus-Release")]
    [InlineData("https://github.com/Ov3rl0rd1/Horus-Release.git")]
    [InlineData("https://github.com/Ov3rl0rd1/Horus-Release/releases/tag/v0.1.0")]
    public void ReadsTheRepositoryOutOfAnyOfTheUsualUrls(string url) =>
        Assert.Equal(
            "https://api.github.com/repos/Ov3rl0rd1/Horus-Release/releases?per_page=20",
            GitHubApi.BuildReleasesUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https://gitlab.com/owner/repo")]
    public void ReturnsNullWhenThereIsNoRepository(string? url) =>
        Assert.Null(GitHubApi.BuildReleasesUrl(url));

    [Fact]
    public void TheConfiguredDefaultResolves() =>
        // Guards the value shipped in appsettings.json against a typo that would silently
        // disable the GitHub source and leave every user on the site fallback.
        Assert.NotNull(GitHubApi.BuildReleasesUrl(
            "https://github.com/Ov3rl0rd1/Horus-Release/releases"));
}
