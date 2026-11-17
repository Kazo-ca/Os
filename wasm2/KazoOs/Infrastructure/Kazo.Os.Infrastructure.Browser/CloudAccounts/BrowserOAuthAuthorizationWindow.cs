using Kazo.Os.Simulation.Abstractions.CloudAccounts;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.CloudAccounts;

/// <summary>
/// Browser half of the cloud account sign-in (ADR 0267 section 5): imports <c>oauthWindow.js</c> of
/// this library, which opens the provider in a pop-up window and waits on a
/// <c>BroadcastChannel</c> for <c>oauth-callback.html</c>, the static page the provider returns to.
/// </summary>
/// <remarks>
/// <para>
/// Both files are assets of this Razor class library
/// (<c>_content/Kazo.Os.Infrastructure.Browser/…</c>), never referenced by a host page. The published
/// service worker serves the callback page as it is, query included, with the isolation headers
/// (ADR 0131), and keeps it in the offline cache with the other assets.
/// </para>
/// <para>
/// Opening a window needs the user's click to be recent: call <see cref="PrepareAsync"/> when the
/// dialog that offers the providers opens, so that <see cref="AuthorizeAsync"/> calls
/// <c>window.open</c> without waiting for the module to load.
/// </para>
/// </remarks>
public sealed class BrowserOAuthAuthorizationWindow : IOAuthAuthorizationWindow, IAsyncDisposable
{
    /// <summary>Path of the callback page, relative to the base address of the OS.</summary>
    public const string CallbackPath = "_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html";

    internal const string ModulePath = "./_content/Kazo.Os.Infrastructure.Browser/oauthWindow.js";

    private readonly IJSRuntime _runtime;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;
    private bool _disposed;

    /// <summary>Creates the window over the page's JavaScript runtime.</summary>
    /// <param name="runtime">Runtime the module is imported with.</param>
    /// <param name="baseAddress">Base address of the OS (the host's <c>&lt;base href&gt;</c>), for example <c>https://os.kazo.ca/</c>.</param>
    public BrowserOAuthAuthorizationWindow(IJSRuntime runtime, Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(baseAddress);
        _runtime = runtime;
        RedirectUri = GetRedirectUri(baseAddress);
    }

    /// <inheritdoc />
    public Uri RedirectUri { get; }

    /// <summary>Gets the callback page's address for an OS served at <paramref name="baseAddress"/>.</summary>
    /// <param name="baseAddress">Base address of the OS; a missing trailing slash is added.</param>
    public static Uri GetRedirectUri(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        string text = baseAddress.GetLeftPart(UriPartial.Path);
        return new Uri(new Uri(text.EndsWith('/') ? text : text + "/"), CallbackPath);
    }

    /// <inheritdoc />
    public async ValueTask PrepareAsync(CancellationToken cancellationToken = default)
    {
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        await module.InvokeVoidAsync("prepare", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OAuthAuthorizationResult> AuthorizeAsync(
        Uri authorizationUrl,
        string state,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);
        ArgumentException.ThrowIfNullOrEmpty(state);
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => _ = CancelAsync(module, state));
        WindowAnswer answer;
        try
        {
            // No cancellation token on the call itself: cancel() resolves the pending promise.
            answer = await module
                .InvokeAsync<WindowAnswer>("authorize", authorizationUrl.AbsoluteUri, state, (long)timeout.TotalMilliseconds)
                .ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            return new OAuthAuthorizationResult(OAuthAuthorizationStatus.Cancelled);
        }

        return ToResult(answer);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The page is gone: nothing to release.
            }
        }

        _moduleGate.Dispose();
    }

    /// <summary>Maps the module's answer to the contract.</summary>
    internal static OAuthAuthorizationResult ToResult(WindowAnswer? answer) => answer?.Status switch
    {
        "authorized" when !string.IsNullOrEmpty(answer.Code) => new OAuthAuthorizationResult(OAuthAuthorizationStatus.Authorized, answer.Code),
        "failed" => new OAuthAuthorizationResult(OAuthAuthorizationStatus.Failed, Error: answer.Error, ErrorDescription: answer.ErrorDescription),
        "blocked" => new OAuthAuthorizationResult(OAuthAuthorizationStatus.Blocked),
        "cancelled" => new OAuthAuthorizationResult(OAuthAuthorizationStatus.Cancelled),
        _ => new OAuthAuthorizationResult(OAuthAuthorizationStatus.TimedOut)
    };

    private static async Task CancelAsync(IJSObjectReference module, string state)
    {
        try
        {
            await module.InvokeVoidAsync("cancel", state).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or ObjectDisposedException)
        {
            // The promise is already settled or the page is gone.
        }
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _module ??= await _runtime
                .InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath)
                .ConfigureAwait(false);
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    /// <summary>What <c>authorize()</c> resolves with.</summary>
    /// <param name="Status"><c>authorized</c>, <c>failed</c>, <c>blocked</c>, <c>timedOut</c> or <c>cancelled</c>.</param>
    /// <param name="Code">The authorization code.</param>
    /// <param name="Error">The provider's error.</param>
    /// <param name="ErrorDescription">The provider's error description.</param>
    internal sealed record WindowAnswer(string? Status, string? Code, string? Error, string? ErrorDescription);
}
