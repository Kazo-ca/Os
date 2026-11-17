namespace Kazo.Os.Simulation.Abstractions.CloudAccounts;

/// <summary>How an OAuth authorization window ended (ADR 0267 section 5).</summary>
public enum OAuthAuthorizationStatus
{
    /// <summary>The provider came back to the callback page with an authorization code.</summary>
    Authorized,

    /// <summary>The provider came back with an error, for example the user refused access.</summary>
    Failed,

    /// <summary>The browser refused to open the window (no user gesture, popup blocker).</summary>
    Blocked,

    /// <summary>No answer came back before the timeout: the user closed the window or left it open.</summary>
    TimedOut,

    /// <summary>The OS stopped waiting (the caller cancelled).</summary>
    Cancelled
}

/// <summary>What the callback page handed back to the OS.</summary>
/// <param name="Status">How the window ended.</param>
/// <param name="Code">The authorization code when <see cref="OAuthAuthorizationStatus.Authorized"/>.</param>
/// <param name="Error">The provider's <c>error</c> value when <see cref="OAuthAuthorizationStatus.Failed"/>.</param>
/// <param name="ErrorDescription">The provider's <c>error_description</c>, when given.</param>
public sealed record OAuthAuthorizationResult(
    OAuthAuthorizationStatus Status,
    string? Code = null,
    string? Error = null,
    string? ErrorDescription = null);

/// <summary>
/// Opens a provider's sign-in page in a browser window and waits for its answer (ADR 0267
/// section 5). The provider returns to a static page of Kazo OS (<see cref="RedirectUri"/>), which
/// hands the code back to the OS and closes. The OS is never navigated away: a full-page redirect
/// would restart it.
/// </summary>
/// <remarks>
/// The browser implementation opens the window first, before any other asynchronous work, so that
/// it still runs inside the user gesture (the click) that asked for it. Browsers block windows that
/// open later.
/// </remarks>
public interface IOAuthAuthorizationWindow
{
    /// <summary>
    /// Gets the address of the callback page, the exact redirect URI the provider must know:
    /// <c>&lt;base&gt;_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html</c>.
    /// </summary>
    Uri RedirectUri { get; }

    /// <summary>Loads what the window needs ahead of the user's click, so that opening it waits on nothing.</summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    ValueTask PrepareAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens <paramref name="authorizationUrl"/> and waits for the callback page that carries <paramref name="state"/>.</summary>
    /// <param name="authorizationUrl">The provider's authorization URL, with every parameter.</param>
    /// <param name="state">The random <c>state</c> sent to the provider; answers with another state are ignored.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>How the window ended.</returns>
    ValueTask<OAuthAuthorizationResult> AuthorizeAsync(
        Uri authorizationUrl,
        string state,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
