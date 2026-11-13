using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>store-ux work item 2: the screenshot viewer's moves, without a browser.</summary>
public sealed class StoreScreenshotViewerStateTests
{
    [Fact]
    public void A_closed_viewer_opens_on_the_screenshot_clicked()
    {
        StoreScreenshotViewerState state = StoreScreenshotViewerState.Closed(3).Open(1);

        Assert.True(state.IsOpen);
        Assert.Equal(1, state.Index);
        Assert.True(state.HasPrevious);
        Assert.True(state.HasNext);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void An_index_outside_the_list_does_not_open_the_viewer(int index)
    {
        Assert.False(StoreScreenshotViewerState.Closed(3).Open(index).IsOpen);
    }

    [Fact]
    public void A_page_without_screenshots_never_opens()
    {
        StoreScreenshotViewerState state = StoreScreenshotViewerState.Closed(-4);

        Assert.Equal(0, state.Count);
        Assert.False(state.Open(0).IsOpen);
    }

    [Fact]
    public void Next_and_previous_stop_at_the_ends_instead_of_looping()
    {
        StoreScreenshotViewerState first = StoreScreenshotViewerState.Closed(2).Open(0);

        Assert.False(first.HasPrevious);
        Assert.Equal(first, first.Previous());
        StoreScreenshotViewerState last = first.Next();
        Assert.Equal(1, last.Index);
        Assert.False(last.HasNext);
        Assert.Equal(last, last.Next());
    }

    [Theory]
    [InlineData("ArrowRight", 2)]
    [InlineData("ArrowLeft", 0)]
    [InlineData("Home", 0)]
    [InlineData("End", 3)]
    [InlineData("Tab", 1)]
    [InlineData(null, 1)]
    public void Keys_move_through_the_screenshots(string? key, int expected)
    {
        StoreScreenshotViewerState state = StoreScreenshotViewerState.Closed(4).Open(1).Key(key);

        Assert.Equal(expected, state.Index);
    }

    [Fact]
    public void Escape_closes_the_viewer()
    {
        Assert.False(StoreScreenshotViewerState.Closed(4).Open(2).Key("Escape").IsOpen);
    }

    [Fact]
    public void Keys_do_nothing_while_the_viewer_is_closed()
    {
        StoreScreenshotViewerState closed = StoreScreenshotViewerState.Closed(4);

        Assert.Equal(closed, closed.Key("ArrowRight"));
        Assert.Equal(closed, closed.Key("End"));
    }

    [Fact]
    public void A_swipe_to_the_left_shows_the_next_screenshot_and_to_the_right_the_previous()
    {
        StoreScreenshotViewerState state = StoreScreenshotViewerState.Closed(3).Open(1);

        Assert.Equal(2, state.Swipe(-120, 10).Index);
        Assert.Equal(0, state.Swipe(120, -10).Index);
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(-60, 90)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void A_tap_a_vertical_move_or_a_broken_measure_is_not_a_swipe(double deltaX, double deltaY)
    {
        StoreScreenshotViewerState state = StoreScreenshotViewerState.Closed(3).Open(1);

        Assert.False(StoreScreenshotViewerState.IsSwipe(deltaX, deltaY));
        Assert.Equal(state, state.Swipe(deltaX, deltaY));
    }
}
