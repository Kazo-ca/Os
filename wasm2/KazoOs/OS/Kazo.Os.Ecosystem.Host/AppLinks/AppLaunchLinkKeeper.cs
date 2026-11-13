using Kazo.Os.Platform.Core.AppLinks;

namespace Kazo.Os.Ecosystem.AppLinks;

/// <summary>
/// Decides which direct link the boot runs and how long the tab keeps it (ADR 0265): the link of
/// the address first, otherwise the one this tab kept before a reload; kept until it has finished.
/// </summary>
/// <remarks>
/// Without a store (a host with no browser), the keeper reads the address only: the behaviour of
/// ADR 0258, where the link lives in memory.
/// </remarks>
internal sealed class AppLaunchLinkKeeper(IAppLaunchLinkStore? store)
{
    /// <summary>
    /// Reads the link to run at this boot (ADR 0265 §2). A valid link of the address wins and
    /// replaces the kept one; otherwise the kept id is checked again, and a malformed one is erased.
    /// </summary>
    /// <param name="address">The page address, before the parameter is removed from it.</param>
    /// <returns>The link to run, or <see langword="null"/> when none waits.</returns>
    public async Task<AppLaunchLink?> ReadAtBootAsync(string? address)
    {
        if (AppLaunchLink.TryParse(address, out AppLaunchLink? fromAddress) && fromAddress is not null)
        {
            if (store is not null)
            {
                await store.SaveAsync(fromAddress.AppId);
            }

            return fromAddress;
        }

        if (store is null || await store.ReadAsync() is not { } kept)
        {
            return null;
        }

        if (AppLaunchLink.TryFromAppId(kept, out AppLaunchLink? fromTab) && fromTab is not null)
        {
            return fromTab;
        }

        // The tab's storage is input too: a value the OS did not write is dropped.
        await store.ClearAsync();
        return null;
    }

    /// <summary>
    /// Forgets the kept link once it has finished (ADR 0265 §3): app launched, Store opened, error
    /// notified or cancelled, or « Later » of the update dialog. A later reload runs nothing.
    /// </summary>
    public async Task FinishAsync()
    {
        if (store is not null)
        {
            await store.ClearAsync();
        }
    }
}
