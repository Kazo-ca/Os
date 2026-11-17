using Kazo.Os.AppSdk.Blazor.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace Kazo.Os.Apps.Settings;

/// <summary>One page of the side menu (ADR 0170): its id for <c>--page=</c>, its group, its label key and its icon.</summary>
internal sealed record SettingsPage(string Id, string GroupKey, string LabelKey, string Icon)
{
    /// <summary>Gets the label of a page a feature adds (ADR 0249 §1), shown as is instead of <see cref="LabelKey"/>.</summary>
    public string? Label { get; init; }

    /// <summary>Gets the URL of the icon of a page a feature adds, or <see langword="null"/> for <see cref="Icon"/>.</summary>
    public string? IconUrl { get; init; }

    /// <summary>Gets whether a feature added the page (ADR 0249 §2): it is drawn by its own process.</summary>
    public bool IsFeaturePage => Label is not null;
}

/// <summary>
/// The side menu of Settings (ADR 0170): a vertical list of grouped pages that the user collapses to an icon
/// rail or expands with labels. In a narrow window (a phone), the menu stays a rail and opens over the page.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Id of the "Appearance" page, the first one (<c>--page=appearance</c>).</summary>
    internal const string AppearancePageId = "appearance";

    /// <summary>Id of the "Desktop" page (<c>--page=desktop</c>, ADR 0160 §8).</summary>
    internal const string DesktopPageId = "desktop";

    /// <summary>Id of the "Backup" page, only listed when the host supports it (ADR 0099).</summary>
    internal const string BackupPageId = "backup";

    /// <summary>Id of the "Application" page, only listed for <c>--app=&lt;id&gt;</c> (ADR 0096 §4).</summary>
    internal const string ApplicationPageId = "application";

    /// <summary>Id of the "Cloud accounts" page (<c>--page=cloud-accounts</c>, ADR 0267 §11).</summary>
    internal const string CloudAccountsPageId = "cloud-accounts";

    /// <summary>Id of the "What's new" page (<c>--page=whats-new</c>, ADR 0186 §4.5), opened by the sign-in notification.</summary>
    internal const string WhatsNewPageId = "whats-new";

    /// <summary>Id of the "Installed features" page (<c>--page=features</c>, ADR 0249 §1).</summary>
    internal const string FeaturesPageId = "features";

    /// <summary>The group of the Features section (ADR 0249 §1, answer 2 A): after Applications, before Network.</summary>
    internal const string FeaturesGroupKey = "Nav.Group.Features";

    /// <summary>Every page in menu order; <see cref="PagesFor"/> drops the optional ones.</summary>
    internal static IReadOnlyList<SettingsPage> AllPages { get; } =
    [
        new(AppearancePageId, "Nav.Group.Personalization", "Tab.Appearance", Icons.Material.Filled.Palette),
        new(DesktopPageId, "Nav.Group.Personalization", "Tab.Desktop", Icons.Material.Filled.DesktopWindows),
        new("screens", "Nav.Group.Personalization", "Tab.Screens", Icons.Material.Filled.Monitor),
        new("accessibility", "Nav.Group.Personalization", "Tab.Accessibility", Icons.Material.Filled.Accessibility),
        new("language", "Nav.Group.Personalization", "Tab.Language", Icons.Material.Filled.Translate),
        new("datetime", "Nav.Group.Personalization", "Tab.DateTime", Icons.Material.Filled.Schedule),
        new("sound", "Nav.Group.Personalization", "Tab.Sound", Icons.Material.Filled.VolumeUp),
        new("notifications", "Nav.Group.Personalization", "Tab.Notifications", Icons.Material.Filled.Notifications),
        new("file-associations", "Nav.Group.Applications", "Tab.FileAssociations", Icons.Material.Filled.Extension),
        new("capabilities", "Nav.Group.Applications", "Tab.CapabilityGrants", Icons.Material.Filled.Security),
        new("permissions", "Nav.Group.Applications", "Tab.Permissions", Icons.Material.Filled.AdminPanelSettings),
        new("installed-apps", "Nav.Group.Applications", "Tab.InstalledApps", Icons.Material.Filled.Apps),
        new("app-sources", "Nav.Group.Applications", "Tab.AppSources", Icons.Material.Filled.Storefront),
        new("os-components", "Nav.Group.Applications", "Tab.OsComponents", Icons.Material.Filled.Widgets),
        new("file-menus", "Nav.Group.Applications", "Tab.FileMenus", Icons.Material.Filled.MenuOpen),
        new(ApplicationPageId, "Nav.Group.Applications", "Tab.Application", Icons.Material.Filled.Info),
        // ADR 0249 §1: the pages features add follow "Installed features" (PagesFor).
        new(FeaturesPageId, FeaturesGroupKey, "Tab.Features", Icons.Material.Filled.Category),
        new("server", "Nav.Group.Network", "Tab.ServerConnection", Icons.Material.Filled.Cloud),
        new("sync", "Nav.Group.Network", "Tab.Synchronization", Icons.Material.Filled.Sync),
        new(CloudAccountsPageId, "Nav.Group.Network", "Tab.CloudAccounts", Icons.Material.Filled.CloudQueue),
        new("users", "Nav.Group.System", "Tab.Users", Icons.Material.Filled.People),
        new("memory", "Nav.Group.System", "Tab.Memory", Icons.Material.Filled.Memory),
        // ADR 0250 §3 (answer 2 A): next to Memory.
        new("storage", "Nav.Group.System", "Tab.Storage", Icons.Material.Filled.Storage),
        new(BackupPageId, "Nav.Group.System", "Tab.Backup", Icons.Material.Filled.Backup),
        new(WhatsNewPageId, "Nav.Group.System", "Tab.WhatsNew", Icons.Material.Filled.NewReleases),
        new("about", "Nav.Group.System", "Tab.About", Icons.Material.Filled.Info),
    ];

    private string _activePage = AppearancePageId;

    // Wide window: the menu is in the flow, expanded with labels unless the user collapses it.
    private bool _navCollapsed;

    // Narrow window: the menu is an icon rail that the user opens over the page; choosing a page closes it.
    private bool _navOverlayOpen;

    private string? _pendingNavFocus;
    private readonly Dictionary<string, ElementReference> _navItemRefs = [];

    /// <summary>
    /// The pages of the menu, without Backup when the host lacks it and without Application when no app is named,
    /// with the pages the installed features add (ADR 0249 §1).
    /// </summary>
    private IReadOnlyList<SettingsPage> VisiblePages => PagesFor(
        Kazo.Os.AppSdk.Administration.KazoSystemBackup.IsSupportedFor(AppContext),
        _appPageId is not null,
        _featurePages);

    /// <summary>
    /// The menu's pages for a host with or without backup, with or without an app page, and with the
    /// pages of the features after "Installed features", each labelled "&lt;Feature&gt; ▸ &lt;Title&gt;".
    /// </summary>
    internal static IReadOnlyList<SettingsPage> PagesFor(
        bool backupSupported, bool hasAppPage, IReadOnlyList<FeatureSettingsPageInfo>? featurePages = null)
    {
        List<SettingsPage> pages = [];
        foreach (SettingsPage page in AllPages)
        {
            bool visible = page.Id switch
            {
                BackupPageId => backupSupported,
                ApplicationPageId => hasAppPage,
                _ => true
            };
            if (!visible)
            {
                continue;
            }

            pages.Add(page);
            if (page.Id == FeaturesPageId)
            {
                pages.AddRange((featurePages ?? []).Select(static featurePage =>
                    new SettingsPage(featurePage.Id, FeaturesGroupKey, string.Empty, Icons.Material.Filled.Tune)
                    {
                        Label = $"{featurePage.FeatureName} ▸ {featurePage.Title}",
                        IconUrl = featurePage.IconUrl,
                    }));
            }
        }

        return pages;
    }

    /// <summary>
    /// Reads <c>--page=&lt;id&gt;</c> naming a page a feature adds (ADR 0249 §4): an entry point id
    /// such as <c>ca.kazo.x86.admin</c>, never a page of Settings itself; the last one wins.
    /// </summary>
    internal static string? ReadFeaturePageArgument(IReadOnlyList<string> arguments) =>
        arguments
            .Where(static argument => argument.StartsWith(PageArgumentPrefix, StringComparison.Ordinal))
            .Select(static argument => argument[PageArgumentPrefix.Length..].Trim())
            .LastOrDefault(static id => id.Contains('.', StringComparison.Ordinal) && AllPages.All(page => page.Id != id));

    /// <summary>The label of a menu entry: a feature's page shows its own, a page of Settings its translation.</summary>
    private string LabelOf(SettingsPage page) => page.Label ?? L[page.LabelKey];

    /// <summary>
    /// Reads <c>--page=&lt;id&gt;</c> (any page id but <c>application</c>, which <c>--app=</c> opens);
    /// the last known page wins, an unknown one is ignored.
    /// </summary>
    internal static string? ReadPageArgument(IReadOnlyList<string> arguments) =>
        arguments
            .Where(static argument => argument.StartsWith(PageArgumentPrefix, StringComparison.Ordinal))
            .Select(static argument => argument[PageArgumentPrefix.Length..].Trim().ToLowerInvariant())
            .LastOrDefault(static id => id != ApplicationPageId && AllPages.Any(page => page.Id == id));

    private void SelectPage(string pageId)
    {
        // ADR 0220 §4: the mouse's Back/Forward buttons and the phone's Back walk through the pages chosen.
        _pageHistory.Visit(_activePage, pageId);
        _activePage = pageId;
        _navOverlayOpen = false;
    }

    private void ToggleNavCollapsed() => _navCollapsed = !_navCollapsed;

    private void ToggleNavOverlay() => _navOverlayOpen = !_navOverlayOpen;

    private void CloseNavOverlay() => _navOverlayOpen = false;

    private string NavClass =>
        "settings-nav" + (_navCollapsed ? " is-collapsed" : string.Empty) + (_navOverlayOpen ? " is-overlay-open" : string.Empty);

    private string NavItemClass(SettingsPage page) =>
        page.Id == _activePage ? "settings-nav-item is-active" : "settings-nav-item";

    private static string NavItemDomId(SettingsPage page) => "settings-nav-" + page.Id;

    /// <summary>Arrow keys, Home and End move between the pages of the vertical tab list (WAI-ARIA tabs pattern).</summary>
    private void OnNavKeyDown(KeyboardEventArgs args)
    {
        IReadOnlyList<SettingsPage> pages = VisiblePages;
        int current = Math.Max(0, pages.ToList().FindIndex(page => page.Id == _activePage));
        int? next = args.Key switch
        {
            "ArrowDown" => (current + 1) % pages.Count,
            "ArrowUp" => (current - 1 + pages.Count) % pages.Count,
            "Home" => 0,
            "End" => pages.Count - 1,
            _ => null
        };
        if (next is { } index)
        {
            _activePage = pages[index].Id;
            _pendingNavFocus = pages[index].Id;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        RaiseNavigationAvailabilityChanged();
        if (_pendingNavFocus is { } id && _navItemRefs.TryGetValue(id, out ElementReference element))
        {
            _pendingNavFocus = null;
            await element.FocusAsync();
        }
    }
}
