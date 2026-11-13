using Kazo.Os.Platform.Core.AppLinks;

namespace Kazo.Os.Platform.Core.Tests.AppLinks;

/// <summary>ADR 0258 §1 and §2: reading a direct app launch link and removing it from the address.</summary>
public sealed class AppLaunchLinkTests
{
    [Theory]
    [InlineData("https://os.kazo.ca/?autolaunch=ca.kazo.word", "ca.kazo.word")]
    [InlineData("https://os.kazo.ca/?lang=fr&autolaunch=org.example.app-2#top", "org.example.app-2")]
    [InlineData("/?autolaunch=a.b", "a.b")]
    [InlineData("https://host/os/?autolaunch=ca%2Ekazo%2Eword", "ca.kazo.word")]
    public void Reads_the_app_id_of_a_valid_link(string address, string appId)
    {
        Assert.True(AppLaunchLink.TryParse(address, out AppLaunchLink? link));
        Assert.Equal(appId, link!.AppId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://os.kazo.ca/")]
    [InlineData("https://os.kazo.ca/?other=ca.kazo.word")]
    [InlineData("https://os.kazo.ca/#?autolaunch=ca.kazo.word")]
    [InlineData("https://os.kazo.ca/?autolunch=ca.kazo.word")]
    [InlineData("https://os.kazo.ca/?AUTOLAUNCH=ca.kazo.word")]
    [InlineData("https://os.kazo.ca/?autolaunch")]
    [InlineData("https://os.kazo.ca/?autolaunch=")]
    [InlineData("https://os.kazo.ca/?autolaunch=word")]
    [InlineData("https://os.kazo.ca/?autolaunch=Ca.Kazo.Word")]
    [InlineData("https://os.kazo.ca/?autolaunch=ca..word")]
    [InlineData("https://os.kazo.ca/?autolaunch=.ca.word")]
    [InlineData("https://os.kazo.ca/?autolaunch=ca.kazo.")]
    [InlineData("https://os.kazo.ca/?autolaunch=https://evil.example/app.kazopkg")]
    [InlineData("https://os.kazo.ca/?autolaunch=ca.kazo/../word")]
    [InlineData("https://os.kazo.ca/?autolaunch=ca.kazo.word%20x")]
    [InlineData("https://os.kazo.ca/?autolaunch=ca%zz.word")]
    public void Ignores_a_missing_or_malformed_link(string? address)
    {
        Assert.False(AppLaunchLink.TryParse(address, out AppLaunchLink? link));
        Assert.Null(link);
    }

    [Fact]
    public void Refuses_an_id_longer_than_128_characters()
    {
        string longest = "a." + new string('b', AppLaunchLink.MaxAppIdLength - 2);
        Assert.True(AppLaunchLink.TryParse("/?autolaunch=" + longest, out _));
        Assert.False(AppLaunchLink.TryParse("/?autolaunch=" + longest + "c", out _));
    }

    [Fact]
    public void Only_the_first_parameter_counts()
    {
        Assert.True(AppLaunchLink.TryParse("/?autolaunch=ca.kazo.word&autolaunch=org.other.app", out AppLaunchLink? link));
        Assert.Equal("ca.kazo.word", link!.AppId);

        // A malformed first parameter is not skipped for a later one.
        Assert.False(AppLaunchLink.TryParse("/?autolaunch=bad&autolaunch=ca.kazo.word", out _));
    }

    [Theory]
    [InlineData("https://os.kazo.ca/?autolaunch=ca.kazo.word", "https://os.kazo.ca/")]
    [InlineData("https://os.kazo.ca/?lang=fr&autolaunch=ca.kazo.word&x=1#top", "https://os.kazo.ca/?lang=fr&x=1#top")]
    [InlineData("https://os.kazo.ca/?autolaunch=a.b&autolaunch=c.d", "https://os.kazo.ca/")]
    [InlineData("https://os.kazo.ca/?autolaunch=bad%zz#frag", "https://os.kazo.ca/#frag")]
    [InlineData("https://os.kazo.ca/?autolaunch", "https://os.kazo.ca/")]
    public void Removes_every_autolaunch_parameter_from_the_address(string address, string expected) =>
        Assert.Equal(expected, AppLaunchLink.RemoveFromAddress(address));

    [Theory]
    [InlineData("https://os.kazo.ca/")]
    [InlineData("https://os.kazo.ca/?lang=fr#x")]
    [InlineData("https://os.kazo.ca/#?autolaunch=a.b")]
    public void Keeps_an_address_without_the_parameter_unchanged(string address) =>
        Assert.Same(address, AppLaunchLink.RemoveFromAddress(address));
}
