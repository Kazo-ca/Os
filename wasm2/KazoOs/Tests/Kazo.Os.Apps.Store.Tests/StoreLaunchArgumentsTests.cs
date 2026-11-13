using Kazo.Os.Apps.Store;
using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>The Store's command line, with the file to open after an install (ADR 0257 §5).</summary>
public sealed class StoreLaunchArgumentsTests
{
    [Fact]
    public void An_app_page_keeps_the_file_to_open_after_its_install()
    {
        StoreLaunchArguments arguments = StoreLaunchArguments.Parse(["--app=ca.kazo.word", "--open-after-install=/home/alice/Report 1.docx"]);

        Assert.Equal(StoreView.Detail, arguments.View);
        Assert.Equal("ca.kazo.word", arguments.AppId);
        Assert.Equal("/home/alice/Report 1.docx", arguments.OpenAfterInstall);
    }

    [Fact]
    public void A_file_without_an_app_page_is_ignored()
    {
        StoreLaunchArguments arguments = StoreLaunchArguments.Parse(["--page=installed", "--open-after-install=/home/alice/report.docx"]);

        Assert.Equal(StoreView.Installed, arguments.View);
        Assert.Null(arguments.AppId);
        Assert.Null(arguments.OpenAfterInstall);
    }

    [Theory]
    [InlineData("--open-after-install=")]
    [InlineData("--open-after-install=report.docx")]
    public void Only_an_absolute_path_is_kept(string argument)
    {
        Assert.Null(StoreLaunchArguments.Parse(["--app=ca.kazo.word", argument]).OpenAfterInstall);
    }

    [Fact]
    public void Tabs_and_unknown_arguments_keep_their_previous_meaning()
    {
        Assert.Equal(StoreView.Themes, StoreLaunchArguments.Parse(["--page=themes"]).View);
        Assert.Null(StoreLaunchArguments.Parse(["--page=unknown", "--verbose"]).View);
        Assert.Null(StoreLaunchArguments.Parse(["--app="]).View);
    }
}
