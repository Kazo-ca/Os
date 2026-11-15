using System.Globalization;
using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Store;

/// <summary>What <see cref="StoreAppPage"/> reads from the window and the actions it runs there.</summary>
public partial class StoreWindow
{
    /// <summary>
    /// An action of the app's page that changes the window's state (the view, the busy state, a
    /// message): the window handles the event, so it draws itself again, and runs it in the Store's
    /// app context like its own buttons.
    /// </summary>
    /// <param name="action">The action.</param>
    internal EventCallback Callback(Func<Task> action) => EventCallback.Factory.Create(this, action);

    /// <inheritdoc cref="Callback(Func{Task})" />
    internal EventCallback Callback(Action action) => EventCallback.Factory.Create(this, action);

    /// <summary>Gets the app whose page is open, or <see langword="null"/>.</summary>
    internal StoreEntry? SelectedEntry => _view == StoreView.Detail ? Selected : null;

    /// <summary>Gets the Store's texts.</summary>
    internal IStringLocalizer<Strings> Text => L;

    /// <summary>Gets the language of the Store, for the catalogs' texts and the dates.</summary>
    internal CultureInfo TextCulture => Culture;

    /// <summary>Gets whether an action runs or waits on this app: only its buttons wait (ADR 0266 §7).</summary>
    /// <param name="entry">The app.</param>
    internal bool IsRunning(StoreEntry entry) => Operations.IsRunning(entry.AppId);

    /// <summary>Gets whether the long description is being read.</summary>
    internal bool IsDescriptionLoading => _isDescriptionLoading;

    /// <summary>Gets the long description in Markdown, or <see langword="null"/>.</summary>
    internal string? Description => _description;

    /// <summary>The file that opens once <paramref name="entry"/> is installed (ADR 0257 §5), or <see langword="null"/>.</summary>
    /// <param name="entry">The app shown.</param>
    internal string? OpenAfterInstallPath(StoreEntry entry) =>
        _openAfterInstall is ({ } appId, { } path) && appId == entry.AppId && entry.State == StoreAppState.NotInstalled ? path : null;

    /// <summary>
    /// Copies the « Try it » link of <paramref name="entry"/> (ADR 0258) on the OS clipboard, which
    /// passes it to the real machine's clipboard (ADR 0065), and says so in a message.
    /// </summary>
    /// <param name="entry">The app.</param>
    /// <param name="pageAddress">The address the OS is served from, or <see langword="null"/> when the page could not read it.</param>
    internal async Task CopyTryLinkAsync(StoreEntry entry, string? pageAddress)
    {
        if (pageAddress is null || !StoreAppFacts.IsWebAddress(pageAddress))
        {
            ShowError(L["Error.CopyFailed", pageAddress ?? "?"]);
            return;
        }

        string link = StoreAppFacts.TryLink(pageAddress, entry.AppId);
        try
        {
            await KazoClipboard.SetTextAsync(link);
            ShowInfo(L["Message.TryLinkCopied", link]);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException or NotSupportedException or InvalidOperationException)
        {
            ShowError(L["Error.CopyFailed", exception.Message]);
        }
    }
}
