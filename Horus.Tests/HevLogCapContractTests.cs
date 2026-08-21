using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Horus.Domain.Models;
using Horus.Protocols;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The log cap is a contract across three artefacts in three languages: a C# constant
/// renders a YAML key, a patch in packaging/android/hev-patches teaches the bridge to read
/// it, and the build script asserts the patched binary carries it.
///
/// <para>Every one of those can break silently. A stock upstream bridge ignores an unknown
/// key without complaint, so dropping the patch produces a working tunnel whose log grows
/// forever — and nobody finds out until a device fills up, months later, on the one user
/// who left verbose logging on. Nothing about that failure points back at this.</para>
///
/// <para>These tests are cheap and pin the two halves that live in this repo. The third —
/// that the shipped <c>.so</c> actually contains the patch — is asserted by
/// <c>build-hev.ps1</c>, because it is the only place the binary exists.</para>
/// </summary>
public class HevLogCapContractTests
{
    private static string Config =>
        HevTunnelConfig.Build("/tmp/hev.log", HevTunnelConfig.DefaultLogLevel, XrayConfig.DefaultSocksPort);

    [Fact]
    public void Generated_config_carries_the_cap()
    {
        var match = Regex.Match(Config, @"^\s*log-max-size:\s*(?<size>\d+)\s*$", RegexOptions.Multiline);

        Assert.True(match.Success, "hev YAML carries no log-max-size; the bridge would append forever.");
        Assert.Equal(HevTunnelConfig.MaxLogBytes, int.Parse(match.Groups["size"].Value));
    }

    [Fact]
    public void Cap_is_large_enough_to_be_useful_and_small_enough_to_be_safe()
    {
        // A cap under a megabyte would discard the context around a failure, which is the
        // whole reason the file exists. Above ten and it stops being a bound worth having on
        // a device whose cache directory is shared with everything else.
        Assert.InRange(HevTunnelConfig.MaxLogBytes, 1 * 1024 * 1024, 10 * 1024 * 1024);
    }

    [Fact]
    public void The_patch_that_implements_it_is_present()
    {
        var dir = Path.Combine(RepoRoot(), "packaging", "android", "hev-patches");
        Assert.True(Directory.Exists(dir), $"Patch directory is missing: {dir}");

        var patches = Directory.GetFiles(dir, "*.patch");
        Assert.NotEmpty(patches);

        // Not a diff review — just that something in there still speaks about the key the
        // config emits. A patch renamed or replaced without carrying this forward is the
        // failure mode being guarded.
        Assert.Contains(patches, p => File.ReadAllText(p).Contains("log-max-size"));
    }

    [Fact]
    public void Build_script_applies_patches_and_verifies_them()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "android", "build-hev.ps1"));

        // Applying without verifying is the trap: git apply succeeding says the text matched,
        // not that the result compiled the key into the binary.
        Assert.Contains("git -C $src apply", script);
        Assert.Contains("log-max-size", script);
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
}
