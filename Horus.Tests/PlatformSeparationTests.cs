using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The shared code carries a few hooks that only Windows fills: its own screens
/// (<c>IPlatformScreens</c>) and a server-ping dial pinned to the physical interface
/// (<c>LatencyProbe.Connector</c>). Android stays on the shared UI and the default dial
/// exactly because nothing outside Windows fills them; these tests keep it that way.
/// </summary>
public class PlatformSeparationTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    private static string AppDir => Path.Combine(RepoRoot(), "Horus");

    /// <summary>
    /// The preprocessor branch each line of <paramref name="source"/> sits in, as the
    /// condition text of the innermost <c>#if</c>/<c>#elif</c> (negated for <c>#else</c>);
    /// empty outside any conditional.
    /// </summary>
    private static List<(string Line, string Branch)> Branches(string source)
    {
        var result = new List<(string, string)>();
        var stack = new Stack<string>();
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#if ")) { stack.Push(line[4..].Trim()); continue; }
            if (line.StartsWith("#elif ")) { stack.Pop(); stack.Push(line[6..].Trim()); continue; }
            if (line.StartsWith("#else")) { var c = stack.Pop(); stack.Push($"!({c})"); continue; }
            if (line.StartsWith("#endif")) { stack.Pop(); continue; }
            result.Add((line, stack.Count == 0 ? string.Empty : stack.Peek()));
        }
        return result;
    }

    [Fact]
    public void Platform_screens_are_registered_for_windows_only()
    {
        var lines = Branches(File.ReadAllText(Path.Combine(AppDir, "MauiProgram.cs")));
        var registrations = lines.Where(l => l.Line.Contains("IPlatformScreens")).ToList();

        Assert.NotEmpty(registrations);
        Assert.All(registrations, r => Assert.Equal("WINDOWS", r.Branch));
    }

    [Fact]
    public void Only_windows_code_replaces_the_server_ping_dial()
    {
        var assignments = Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"LatencyProbe\.Connector\s*="))
            .Select(f => Path.GetRelativePath(AppDir, f).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["Platforms/Windows/WindowsVpnController.cs"], assignments);
    }

    [Fact]
    public void The_root_page_falls_back_to_the_shared_screens_without_a_platform()
    {
        // Resolved through DI with nothing registered on Android: the parameter must stay
        // optional, or the app would not start there.
        var source = File.ReadAllText(Path.Combine(AppDir, "Presentation", "View", "RootPage.xaml.cs"));
        Assert.Matches(@"IPlatformScreens\?\s+screens\s*=\s*null", source);
        Assert.Contains("if (screens is not null) AttachPlatformScreens(screens);", source);
    }
}
