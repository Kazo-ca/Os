using Kazo.Os.Infrastructure.Browser.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.CloudAccounts;

namespace Kazo.Os.Infrastructure.Browser.Tests.CloudAccounts;

/// <summary>ADR 0267 section 5: the redirect URI the providers must know, and the answers of <c>oauthWindow.js</c>.</summary>
public sealed class BrowserOAuthAuthorizationWindowTests
{
    [Theory]
    [InlineData("https://os.kazo.ca/", "https://os.kazo.ca/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html")]
    [InlineData("https://os.kazo.ca/?autolaunch=org.kazoos.store", "https://os.kazo.ca/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html")]
    [InlineData("http://localhost:5000/", "http://localhost:5000/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html")]
    [InlineData("https://example.test/kazo", "https://example.test/kazo/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html")]
    public void The_redirect_uri_is_the_callback_page_under_the_base_address(string baseAddress, string expected)
    {
        Assert.Equal(new Uri(expected), BrowserOAuthAuthorizationWindow.GetRedirectUri(new Uri(baseAddress)));
    }

    [Fact]
    public void The_module_answers_map_to_the_contract()
    {
        Assert.Equal(
            new OAuthAuthorizationResult(OAuthAuthorizationStatus.Authorized, "code-1"),
            BrowserOAuthAuthorizationWindow.ToResult(new("authorized", "code-1", null, null)));
        Assert.Equal(
            new OAuthAuthorizationResult(OAuthAuthorizationStatus.Failed, Error: "access_denied", ErrorDescription: "no"),
            BrowserOAuthAuthorizationWindow.ToResult(new("failed", null, "access_denied", "no")));
        Assert.Equal(OAuthAuthorizationStatus.Blocked, BrowserOAuthAuthorizationWindow.ToResult(new("blocked", null, null, null)).Status);
        Assert.Equal(OAuthAuthorizationStatus.Cancelled, BrowserOAuthAuthorizationWindow.ToResult(new("cancelled", null, null, null)).Status);
        Assert.Equal(OAuthAuthorizationStatus.TimedOut, BrowserOAuthAuthorizationWindow.ToResult(new("timedOut", null, null, null)).Status);
        // An "authorized" answer without a code is not a sign-in.
        Assert.Equal(OAuthAuthorizationStatus.TimedOut, BrowserOAuthAuthorizationWindow.ToResult(new("authorized", "", null, null)).Status);
        Assert.Equal(OAuthAuthorizationStatus.TimedOut, BrowserOAuthAuthorizationWindow.ToResult(null).Status);
    }
}
