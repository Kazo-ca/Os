namespace Kazo.Os.Ecosystem.AppLinks;

/// <summary>
/// Where the tab keeps the app id of a direct link between two loads of the page (ADR 0265 §1), so a
/// restart for an update, or any reload before the link has finished, does not lose it.
/// </summary>
/// <remarks>
/// Implementations never throw: a storage the browser refuses behaves as an empty one, and the link
/// then lives in memory only, as in ADR 0258.
/// </remarks>
internal interface IAppLaunchLinkStore
{
    /// <summary>Reads the kept app id, or <see langword="null"/> when none is kept.</summary>
    ValueTask<string?> ReadAsync();

    /// <summary>Keeps <paramref name="appId"/> for the next loads of this tab.</summary>
    /// <param name="appId">The app id of the link.</param>
    ValueTask SaveAsync(string appId);

    /// <summary>Forgets the kept app id.</summary>
    ValueTask ClearAsync();
}
