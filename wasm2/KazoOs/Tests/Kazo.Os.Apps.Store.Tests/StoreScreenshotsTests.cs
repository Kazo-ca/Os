using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>
/// store-ux work item 2: an app's screenshots are buttons that open a viewer at the size of the
/// Store window, with previous/next, Escape, and the Store's Back.
/// </summary>
public sealed class StoreScreenshotsTests
{
    private static readonly string[] ThreeScreenshots =
    [
        "https://example.test/one.png",
        "https://example.test/two.png",
        "https://example.test/three.png",
    ];

    [Fact]
    public async Task Every_screenshot_is_a_button_that_names_its_place_and_the_app()
    {
        (StoreTestRenderer renderer, _, _) = await RenderAsync();

        RenderedElement[] thumbnails = [.. renderer.Elements().Where(element => element.HasClass("store-screenshot-thumb"))];
        Assert.Equal(3, thumbnails.Length);
        Assert.All(thumbnails, thumbnail =>
        {
            Assert.Equal("button", thumbnail.Name);
            Assert.Equal("button", thumbnail["type"]);
            Assert.Equal("dialog", thumbnail["aria-haspopup"]);
        });
        Assert.Equal("Enlarge screenshot 2 of 3 of Kazo Test", thumbnails[1]["aria-label"]);
        Assert.Contains(renderer.Elements(), element => element.Name == "img" && element["src"] == ThreeScreenshots[2]);
        Assert.DoesNotContain(renderer.Elements(), element => element.HasClass("store-viewer"));
    }

    [Fact]
    public async Task A_click_opens_the_viewer_on_that_screenshot_and_tells_the_window()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, List<bool> changes) = await RenderAsync();

        await renderer.RunAsync(() => screenshots.OpenAsync(1));

        Assert.True(screenshots.IsViewerOpen);
        Assert.Equal([true], changes);
        RenderedElement dialog = Assert.Single(renderer.Elements(), element => element.HasClass("store-viewer"));
        Assert.Equal("dialog", dialog["role"]);
        Assert.Equal("true", dialog["aria-modal"]);
        Assert.Equal("Screenshots of Kazo Test", dialog["aria-label"]);
        RenderedElement image = Assert.Single(renderer.Elements(), element => element.HasClass("store-viewer-image"));
        Assert.Equal(ThreeScreenshots[1], image["src"]);
        Assert.Equal("Screenshot 2 of 3 of Kazo Test", image["alt"]);
        Assert.Contains(renderer.Elements(), element => element.HasClass("store-viewer-position"));
    }

    [Fact]
    public async Task Previous_is_disabled_on_the_first_screenshot_and_next_on_the_last()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, _) = await RenderAsync();

        await renderer.RunAsync(() => screenshots.OpenAsync(0));
        Assert.Equal("true", Button(renderer, "store-viewer-previous")["disabled"]);
        Assert.Null(Button(renderer, "store-viewer-next")["disabled"]);

        await renderer.RunAsync(() => screenshots.OnKeyDownAsync(new KeyboardEventArgs { Key = "End" }));
        Assert.Null(Button(renderer, "store-viewer-previous")["disabled"]);
        Assert.Equal("true", Button(renderer, "store-viewer-next")["disabled"]);
        Assert.Equal(ThreeScreenshots[2], Assert.Single(renderer.Elements(), element => element.HasClass("store-viewer-image"))["src"]);
    }

    [Fact]
    public async Task The_arrow_keys_and_the_buttons_move_between_the_screenshots()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, _) = await RenderAsync();

        await renderer.RunAsync(() => screenshots.OpenAsync(0));
        await renderer.RunAsync(() => screenshots.OnKeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" }));
        Assert.Equal(1, screenshots.State.Index);
        await renderer.RunAsync(screenshots.NextAsync);
        Assert.Equal(2, screenshots.State.Index);
        await renderer.RunAsync(screenshots.PreviousAsync);
        await renderer.RunAsync(() => screenshots.OnKeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft" }));
        Assert.Equal(0, screenshots.State.Index);
    }

    [Fact]
    public async Task Escape_closes_the_viewer_and_tells_the_window()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, List<bool> changes) = await RenderAsync();

        await renderer.RunAsync(() => screenshots.OpenAsync(2));
        await renderer.RunAsync(() => screenshots.OnKeyDownAsync(new KeyboardEventArgs { Key = "Escape" }));

        Assert.False(screenshots.IsViewerOpen);
        Assert.Equal([true, false], changes);
        Assert.DoesNotContain(renderer.Elements(), element => element.HasClass("store-viewer"));
    }

    [Fact]
    public async Task Alt_left_is_the_OSs_back_and_not_a_move_in_the_viewer()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, _) = await RenderAsync();

        await renderer.RunAsync(() => screenshots.OpenAsync(2));
        await renderer.RunAsync(() => screenshots.OnKeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft", AltKey = true }));

        Assert.Equal(2, screenshots.State.Index);
    }

    [Fact]
    public async Task Closing_a_closed_viewer_changes_nothing()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, List<bool> changes) = await RenderAsync();

        await renderer.RunAsync(screenshots.CloseAsync);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task Another_apps_screenshots_close_the_viewer()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, _) = await RenderAsync();
        await renderer.RunAsync(() => screenshots.OpenAsync(2));

        await renderer.RunAsync(() => screenshots.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(StoreScreenshots.Uris)] = new[] { "https://example.test/other.png" },
            [nameof(StoreScreenshots.AppName)] = "Other",
        })));

        Assert.False(screenshots.IsViewerOpen);
        Assert.Equal(1, screenshots.State.Count);
    }

    [Fact]
    public async Task A_page_that_goes_away_leaves_the_viewer_closed()
    {
        (StoreTestRenderer renderer, StoreScreenshots screenshots, _) = await RenderAsync();
        await renderer.RunAsync(() => screenshots.OpenAsync(0));

        screenshots.Dispose();

        Assert.False(screenshots.IsViewerOpen);
    }

    private static RenderedElement Button(StoreTestRenderer renderer, string className) =>
        Assert.Single(renderer.Elements(), element => element.HasClass(className));

    private static async Task<(StoreTestRenderer Renderer, StoreScreenshots Screenshots, List<bool> Changes)> RenderAsync()
    {
        StoreTestRenderer renderer = new();
        List<bool> changes = [];
        StoreScreenshots screenshots = new()
        {
            Uris = ThreeScreenshots,
            AppName = "Kazo Test",
            ViewerOpenChanged = EventCallback.Factory.Create<bool>(new object(), changes.Add),
        };

        // The parameters set above are applied by the first render, as a parent would pass them.
        await renderer.RenderAsync(screenshots);
        await renderer.RunAsync(() => screenshots.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(StoreScreenshots.Uris)] = ThreeScreenshots,
            [nameof(StoreScreenshots.AppName)] = "Kazo Test",
            [nameof(StoreScreenshots.ViewerOpenChanged)] = EventCallback.Factory.Create<bool>(new object(), changes.Add),
        })));
        return (renderer, screenshots, changes);
    }
}
