using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// Whatever the Windows client changes in the system has to go away with its process.
///
/// <para>A persistent NRPT rule bound every name lookup on the machine to the TUN's resolver.
/// When the app died — a crash, or Windows shutting down with the VPN on — the rule stayed,
/// the resolver did not, and after a reboot nothing resolved until Horus was started again
/// and swept it. The core's own WFP filters do the same job in a dynamic session, which
/// Windows deletes when the process ends.</para>
/// </summary>
public class WindowsSystemFootprintTests
{
    private static readonly Regex[] PersistentChanges =
    [
        new(@"\bAdd-DnsClientNrptRule\b", RegexOptions.IgnoreCase),
        new(@"\bSet-DnsClientServerAddress\b", RegexOptions.IgnoreCase),
        new(@"\bNew-NetRoute\b", RegexOptions.IgnoreCase),
        new(@"\bnetsh\s+interface\b", RegexOptions.IgnoreCase),
        new(@"\broute\W+(-p\W+)?add\b", RegexOptions.IgnoreCase)
    ];

    private static string WindowsDir([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Horus", "Platforms", "Windows"));

    [Fact]
    public void Nothing_the_windows_client_installs_outlives_its_process()
    {
        var offenders = Directory.EnumerateFiles(WindowsDir(), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => PersistentChanges
                .Where(change => change.IsMatch(File.ReadAllText(file)))
                .Select(change => $"{Path.GetFileName(file)}: {change}"))
            .ToList();

        Assert.Empty(offenders);
    }
}
