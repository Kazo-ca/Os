using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>Where the Store's page scrolls when the view changes, and the tabs' arrow keys.</summary>
public sealed class StoreNavigationTests
{
    [Fact]
    public void Each_page_has_its_own_scroll_key()
    {
        Assert.Equal("home", StoreScrollMemory.ViewKey(StoreView.Home, null, null, searching: false));
        Assert.Equal("search", StoreScrollMemory.ViewKey(StoreView.Home, null, "graphics", searching: true));
        Assert.Equal("category:graphics", StoreScrollMemory.ViewKey(StoreView.Home, null, "graphics", searching: false));
        Assert.Equal("installed", StoreScrollMemory.ViewKey(StoreView.Installed, null, null, searching: true));
        Assert.Equal("themes", StoreScrollMemory.ViewKey(StoreView.Themes, null, null, searching: false));
        Assert.Equal("detail:org.example.notes", StoreScrollMemory.ViewKey(StoreView.Detail, "org.example.notes", null, searching: false));
    }

    [Theory]
    [InlineData(null, "home", false)]
    [InlineData("home", "detail:org.example.notes", false)]
    [InlineData("home", "installed", false)]
    [InlineData("detail:org.example.notes", "home", true)]
    [InlineData("detail:org.example.notes", "installed", true)]
    [InlineData("detail:org.example.notes", "detail:org.example.paint", false)]
    public void Only_a_list_left_for_an_apps_page_comes_back_where_it_was(string? previous, string key, bool restore)
    {
        Assert.Equal(restore, StoreScrollMemory.ShouldRestore(previous, key));
    }

    [Theory]
    [InlineData("Home", "ArrowRight", "Themes")]
    [InlineData("Detail", "ArrowRight", "Themes")]
    [InlineData("Themes", "ArrowRight", "Installed")]
    [InlineData("Installed", "ArrowRight", "Home")]
    [InlineData("Home", "ArrowLeft", "Installed")]
    [InlineData("Installed", "Home", "Home")]
    [InlineData("Home", "End", "Installed")]
    public void The_arrow_keys_go_round_the_tabs(string current, string key, string expected)
    {
        Assert.Equal(Enum.Parse<StoreView>(expected), StoreWindow.TabAfterKey(Enum.Parse<StoreView>(current), key));
    }

    [Theory]
    [InlineData("Home", "ArrowRight", "Installed")]
    [InlineData("Installed", "ArrowRight", "Home")]
    [InlineData("Home", "ArrowLeft", "Installed")]
    [InlineData("Detail", "End", "Installed")]
    public void The_arrow_keys_skip_the_hidden_themes_tab(string current, string key, string expected)
    {
        Assert.Equal(
            Enum.Parse<StoreView>(expected),
            StoreWindow.TabAfterKey(Enum.Parse<StoreView>(current), key, StoreWindow.VisibleTabs(showThemes: false)));
    }

    [Fact]
    public void Other_keys_leave_the_tab_alone()
    {
        Assert.Null(StoreWindow.TabAfterKey(StoreView.Home, "Enter"));
        Assert.Null(StoreWindow.TabAfterKey(StoreView.Home, "ArrowDown"));
    }
}
