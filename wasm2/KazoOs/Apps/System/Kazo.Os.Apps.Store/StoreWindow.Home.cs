using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Apps.Store;

/// <summary>
/// The Store's home page (ADR 0266 §2, §3 and §5): the « Featured » banner, the category filters,
/// one grid of every app newest first, the « Install » button of a card with its menu « Install for
/// everyone » for an administrator, and the « Themes » tab hidden while no pack exists.
/// </summary>
public partial class StoreWindow
{
    /// <summary>Empty cards drawn while the catalogs load (ADR 0266 §2), in place of a word.</summary>
    internal const int SkeletonCardCount = 6;

    /// <summary>Prefix of the menu key of the « Featured » banner: the same app also has a card.</summary>
    private const string FeaturedMenuPrefix = "featured:";

    private readonly Dictionary<string, ElementReference> _installMenuToggles = new(StringComparer.Ordinal);
    private ElementReference _installMenuItem;
    private string? _installMenuKey;
    private bool _focusInstallMenuItem;
    private bool _isAdministrator;

    /// <summary>
    /// Whether the « Themes » tab is shown (ADR 0266 §5): only when a catalog offers a pack or a
    /// pack is installed. An OS without theme packs never shows it.
    /// </summary>
    internal bool ShowsThemesTab => !_themePacksUnsupported && _themePacks.All.Count > 0;

    /// <summary>
    /// Whether the session user is an administrator (<see cref="KazoUserInfo.IsAdministrator"/>):
    /// only then does a card offer « Install for everyone » (ADR 0266 §3). The OS still asks for an
    /// administrator itself (ADR 0189 §4): the Store only hides an option that would be refused.
    /// </summary>
    internal bool OffersInstallForEveryone => _isAdministrator;

    /// <summary>Reads once who the session user is; a window without a context (a test) is not an administrator.</summary>
    private void ReadUser() =>
        _isAdministrator = AppContext is { } context && KazoUser.Describe(context).IsAdministrator;

    /// <summary>
    /// <c>--page=themes</c>, or a click made just before the last pack went away, leads home while
    /// the « Themes » tab is hidden (ADR 0266 §5).
    /// </summary>
    private void LeaveHiddenThemesTab()
    {
        if (_view == StoreView.Themes && !ShowsThemesTab)
        {
            _view = StoreView.Home;
        }
    }

    /// <summary>The « All » filter: every app again.</summary>
    private void ShowAllCategories() => _categoryId = null;

    /// <summary>The label of a card's category: the catalog's, or « Other » when no catalog names it.</summary>
    internal string CardCategoryLabel(StoreEntry entry) =>
        entry.App is { } app && _store.IsNamedCategory(app.Category) ? CategoryLabel(app.Category) : L["Category.Other"];

    /// <summary>The menu key of an app's button: its card, or the « Featured » banner.</summary>
    private static string InstallMenuKey(StoreEntry entry, bool featured) => featured ? FeaturedMenuPrefix + entry.AppId : entry.AppId;

    private bool IsInstallMenuOpen(string key) => _installMenuKey == key;

    /// <summary>Opens the menu of the « Install » button, or closes it when it is open (the WAI-ARIA menu button).</summary>
    private void ToggleInstallMenu(string key)
    {
        if (_installMenuKey == key)
        {
            _installMenuKey = null;
            return;
        }

        _installMenuKey = key;
        _focusInstallMenuItem = true;
    }

    /// <summary>Closes the menu, by a click beside it or after its choice.</summary>
    private void CloseInstallMenu() => _installMenuKey = null;

    /// <summary>Escape closes the menu and gives the focus back to its button.</summary>
    private async Task OnInstallMenuKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key != "Escape" || _installMenuKey is not { } key)
        {
            return;
        }

        _installMenuKey = null;
        if (_installMenuToggles.TryGetValue(key, out ElementReference toggle))
        {
            await FocusQuietlyAsync(toggle);
        }
    }

    /// <summary>The menu's only choice: install for every user of the OS.</summary>
    private Task InstallForEveryoneFromMenuAsync(StoreEntry entry)
    {
        _installMenuKey = null;
        return InstallAsync(entry, KazoPackageScope.System);
    }

    /// <summary>An open menu puts the focus on its first item, as the menu button pattern does.</summary>
    private async Task FocusInstallMenuItemAsync()
    {
        if (!_focusInstallMenuItem || _installMenuKey is null)
        {
            return;
        }

        _focusInstallMenuItem = false;
        await FocusQuietlyAsync(_installMenuItem);
    }

    /// <summary>Moves the focus; without JavaScript (a test renderer) nothing moves.</summary>
    private static async Task FocusQuietlyAsync(ElementReference element)
    {
        try
        {
            await element.FocusAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.JSInterop.JSException or NotSupportedException or ArgumentException)
        {
            // Nothing to focus: the element is gone or there is no browser.
        }
    }
}
