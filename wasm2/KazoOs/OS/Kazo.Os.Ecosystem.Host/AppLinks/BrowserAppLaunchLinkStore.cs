using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.AppLinks;

/// <summary>
/// Keeps the app id of a direct link in the tab's <c>sessionStorage</c> (ADR 0265 §1): it survives
/// reloads of the tab, not its closing, and other tabs do not see it. The calls go straight to the
/// browser's <c>sessionStorage</c> object through Blazor's JS interop: no module is needed.
/// </summary>
internal sealed class BrowserAppLaunchLinkStore(IJSRuntime runtime) : IAppLaunchLinkStore
{
    /// <summary>The <c>sessionStorage</c> key that holds the app id.</summary>
    public const string StorageKey = "kazoos.appLaunchLink";

    /// <inheritdoc />
    public async ValueTask<string?> ReadAsync()
    {
        try
        {
            return await runtime.InvokeAsync<string?>("sessionStorage.getItem", StorageKey);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        try
        {
            await runtime.InvokeVoidAsync("sessionStorage.setItem", StorageKey, appId);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Refused storage (strict private browsing, quota): the link lives in memory only.
        }
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync()
    {
        try
        {
            await runtime.InvokeVoidAsync("sessionStorage.removeItem", StorageKey);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Nothing kept, or nothing reachable: there is nothing left to forget.
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException;
}
