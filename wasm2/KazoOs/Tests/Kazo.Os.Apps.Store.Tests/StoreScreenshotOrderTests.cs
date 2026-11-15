using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>ADR 0266 §8: on a phone, the portrait screenshots come before the landscape ones.</summary>
public sealed class StoreScreenshotOrderTests
{
    private static readonly StoreScreenshotSize Landscape = new(1280, 800);
    private static readonly StoreScreenshotSize Portrait = new(390, 844);

    [Fact]
    public void On_a_phone_portrait_screenshots_move_first_and_each_group_keeps_the_catalogs_order()
    {
        StoreScreenshotSize?[] sizes = [Landscape, Portrait, Landscape, Portrait];

        int[] order = StoreScreenshotOrder.Arrange(sizes, portraitFirst: true);

        Assert.Equal([1, 3, 0, 2], order);
    }

    [Fact]
    public void Away_from_a_phone_the_catalogs_order_is_kept()
    {
        StoreScreenshotSize?[] sizes = [Landscape, Portrait];

        Assert.Equal([0, 1], StoreScreenshotOrder.Arrange(sizes, portraitFirst: false));
    }

    [Fact]
    public void A_screenshot_whose_size_is_unknown_counts_as_landscape()
    {
        StoreScreenshotSize?[] sizes = [null, new StoreScreenshotSize(0, 0), Portrait];

        Assert.Equal([2, 0, 1], StoreScreenshotOrder.Arrange(sizes, portraitFirst: true));
        Assert.False(StoreScreenshotOrder.IsPortrait(null));
        Assert.False(StoreScreenshotOrder.IsPortrait(new StoreScreenshotSize(0, 10)));
    }

    [Fact]
    public void A_square_screenshot_is_not_portrait()
    {
        Assert.False(StoreScreenshotOrder.IsPortrait(new StoreScreenshotSize(500, 500)));
        Assert.True(StoreScreenshotOrder.IsPortrait(new StoreScreenshotSize(500, 501)));
    }

    [Fact]
    public void No_screenshot_gives_an_empty_order()
    {
        Assert.Empty(StoreScreenshotOrder.Arrange([], portraitFirst: true));
    }
}
