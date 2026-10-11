using Horus.Platforms.Windows.Views;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The Windows window has one size and the home screen does not scroll, so these pin what
/// happens on the screens where that could go wrong: laptops at high scaling and small monitors.
/// Work areas are in effective pixels (physical ÷ scale, minus the taskbar).
/// </summary>
public class HomeLayoutTests
{
    [Theory]
    [InlineData(1920, 1032)]   // 1080p at 100%
    [InlineData(2560, 1392)]   // 1440p at 100%
    [InlineData(1280, 672)]    // 1080p laptop at 150%
    [InlineData(1366, 728)]    // 1366×768 at 100%
    public void A_screen_with_room_gets_the_full_window(double w, double h)
    {
        var (width, height) = HomeLayout.FitWindow(w, h);
        Assert.Equal(HomeLayout.WindowWidth, width);
        Assert.Equal(HomeLayout.WindowHeight, height);
    }

    [Theory]
    [InlineData(1093, 582)]    // 1366×768 at 125%
    [InlineData(1024, 536)]    // 1280×720 at 125%
    [InlineData(1097, 571)]    // 1366×768 at 125% with a 40 px taskbar on top of it
    [InlineData(853, 432)]     // 1280×720 at 150%
    public void A_small_screen_gets_the_window_inside_its_work_area(double w, double h)
    {
        var (width, height) = HomeLayout.FitWindow(w, h);
        Assert.True(width <= w && height <= h, $"{width}×{height} in {w}×{h}");
        Assert.True(width >= HomeLayout.MinWindowWidth || width == w);
        Assert.True(height >= HomeLayout.MinWindowHeight || height == h);
    }

    [Fact]
    public void A_tiny_screen_gets_all_of_its_work_area_rather_than_less()
    {
        var (width, height) = HomeLayout.FitWindow(700, 450);
        Assert.Equal(700, width);
        Assert.Equal(450, height);
    }

    [Theory]
    [InlineData(1000, HomeLayout.MaxRing)]
    [InlineData(560, HomeLayout.MaxRing)]   // the full window: content ≈ 600 high, minus padding
    [InlineData(450, 450 - HomeLayout.ConnectColumnFixed)]
    [InlineData(200, HomeLayout.MinRing)]
    public void The_ring_takes_the_height_the_column_has_left(double column, double expected) =>
        Assert.Equal(expected, HomeLayout.RingDiameter(column));

    [Theory]
    [InlineData(HomeLayout.MinWindowHeight)]
    [InlineData(432)]   // 1280×720 at 150%: the whole work area, below the minimum
    public void On_the_smallest_window_the_connect_column_still_fits(double window)
    {
        // The title bar takes about 32, the home screen's padding 42.
        var column = window - 32 - 42;
        Assert.True(HomeLayout.RingDiameter(column) + HomeLayout.ConnectColumnFixed <= column + 1,
            $"ring {HomeLayout.RingDiameter(column)} + {HomeLayout.ConnectColumnFixed} in {column}");
    }
}
