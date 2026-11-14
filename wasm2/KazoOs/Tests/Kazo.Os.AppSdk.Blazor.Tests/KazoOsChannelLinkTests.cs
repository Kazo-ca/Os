using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.Components;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0271 §3 and §6: the channel comes from the version, and each channel links to the other's
/// site. A wrong answer would send stable users to an unannounced beta, or leave a beta user with
/// no way back.
/// </summary>
public sealed class KazoOsChannelLinkTests
{
    [Theory]
    [InlineData("3.4.0-preview.1", true)]
    [InlineData("3.4.0-preview.12", true)]
    [InlineData("3.3.4", false)]
    [InlineData("three", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_version_with_a_prerelease_suffix_is_a_beta(string? version, bool expected) =>
        Assert.Equal(expected, KazoOsChannelLink.IsBeta(version));

    [Theory]
    [InlineData("https://os-beta.kazo.ca/")]
    [InlineData("https://os.kazo.ca/?autolaunch=word")]
    [InlineData("http://localhost:5000/")]
    public void A_beta_always_leads_back_to_the_stable_site(string page)
    {
        Uri? target = KazoOsChannelLink.GetSwitchTarget(SemanticVersion.Parse("3.4.0-preview.1"), new Uri(page));

        Assert.Equal(new Uri(KazoOsChannelLink.StableSiteAddress), target);
    }

    [Theory]
    [InlineData("https://os.kazo.ca/")]
    [InlineData("https://OS.KAZO.CA/settings")]
    public void The_stable_site_offers_the_beta(string page)
    {
        Uri? target = KazoOsChannelLink.GetSwitchTarget(SemanticVersion.Parse("3.3.4"), new Uri(page));

        Assert.Equal(new Uri(KazoOsChannelLink.BetaSiteAddress), target);
    }

    [Theory]
    [InlineData("http://localhost:5000/")]
    [InlineData("https://os.kazo.ca.example.com/")]
    [InlineData("https://example.com/?host=os.kazo.ca")]
    public void A_stable_version_elsewhere_offers_no_link(string page) =>
        Assert.Null(KazoOsChannelLink.GetSwitchTarget(SemanticVersion.Parse("3.3.4"), new Uri(page)));
}
