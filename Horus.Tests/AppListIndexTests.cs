using Horus.Application;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The jump strip beside the app list.
///
/// <para>These exist because the version they replace was <c>Distinct().Take(28)</c>, and 28
/// is "#" plus A–Z plus one. On a Russian device the list is ordered Latin-run-then-
/// Cyrillic-run, so the cut fell exactly where the Cyrillic letters start and every
/// Russian-named app dropped off the strip — while the one Cyrillic letter that survived was
/// pixel-identical to a Latin one, so the result looked like a normal alphabet rather than a
/// truncated list. Nothing about that was visible from the code; it needed a test.</para>
/// </summary>
public class AppListIndexTests
{
    private static readonly string[] Latin =
        [.. "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString())];

    private static readonly string[] Cyrillic =
        [.. "АБВГДЕЖЗИКЛМНОПРСТУФХЦЧШЭЮЯ".Select(c => c.ToString())];

    [Fact]
    public void Both_alphabets_survive()
    {
        var index = AppListIndex.Build([.. Latin, .. Cyrillic]);

        // The specific failure: the Cyrillic run used to be cut off entirely.
        Assert.Contains("Я", index);
        Assert.Contains("A", index);
    }

    [Fact]
    public void A_script_change_is_marked()
    {
        var index = AppListIndex.Build(["A", "B", "А", "Б"]);

        // Without a break, Cyrillic А sits next to Latin B looking like a second A.
        var gap = index.ToList().IndexOf(AppListIndex.Gap);
        Assert.True(gap > 0, "expected a gap between the Latin and Cyrillic runs");
        Assert.Equal("B", index[gap - 1]);
        Assert.Equal("А", index[gap + 1]);
    }

    [Fact]
    public void The_strip_never_outgrows_its_room()
    {
        var index = AppListIndex.Build([.. Latin, .. Cyrillic, "#"]);
        Assert.True(index.Count <= AppListIndex.MaxSlots, $"strip has {index.Count} entries");
    }

    [Fact]
    public void Thinning_keeps_both_ends()
    {
        var index = AppListIndex.Build([.. Latin, .. Cyrillic]);

        // Truncation is what dropped one end. Whatever survives must still span the list, or
        // a drag cannot reach all of it.
        Assert.Equal("A", index[0]);
        Assert.Equal("Я", index[^1]);
    }

    [Fact]
    public void A_short_list_is_left_alone()
    {
        var index = AppListIndex.Build(["A", "B", "C"]);
        Assert.Equal(["A", "B", "C"], index);
    }

    [Fact]
    public void Order_follows_the_caller_not_the_alphabet()
    {
        // The strip has to agree with the list it scrolls, and that list is ordered by the
        // platform's collation — not something to re-derive here.
        var index = AppListIndex.Build(["C", "A", "B"]);
        Assert.Equal(["C", "A", "B"], index);
    }

    [Fact]
    public void Duplicates_and_blanks_are_dropped()
    {
        var index = AppListIndex.Build(["A", "A", "", "B", "A"]);
        Assert.Equal(["A", "B"], index);
    }

    [Theory]
    [InlineData("A", "latin")]
    [InlineData("Z", "latin")]
    [InlineData("А", "cyrillic")]   // U+0410, not U+0041
    [InlineData("М", "cyrillic")]   // the homoglyph that started this
    [InlineData("#", "#")]
    [InlineData("1", "#")]
    public void Scripts_are_told_apart(string key, string expected) =>
        Assert.Equal(expected, AppListIndex.ScriptOf(key));

    [Fact]
    public void Homoglyphs_are_not_the_same_bucket()
    {
        // "M" and "М" render identically and must still be two entries with a break between.
        var index = AppListIndex.Build(["M", "М"]);
        Assert.Equal(3, index.Count);
        Assert.Equal(AppListIndex.Gap, index[1]);
    }
}
