using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Apps.Store;

/// <summary>
/// The Store's keyboard: the arrow keys move between the tabs (the WAI-ARIA tabs pattern), and
/// Enter in the search box opens the first result.
/// </summary>
public partial class StoreWindow
{
    /// <summary>The tabs, left to right.</summary>
    internal static readonly IReadOnlyList<StoreView> TabOrder = [StoreView.Home, StoreView.Themes, StoreView.Installed];

    private readonly string _idPrefix = "store-" + Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<StoreView, ElementReference> _tabElements = [];

    /// <summary>The tab of a view: an app's page belongs to « Home ».</summary>
    internal static StoreView TabOf(StoreView view) => view == StoreView.Detail ? StoreView.Home : view;

    /// <summary>
    /// The tab a key moves to from <paramref name="current"/>: ← and → go round, Home and End go to
    /// the first and the last; any other key moves nowhere.
    /// </summary>
    /// <param name="current">The view on screen.</param>
    /// <param name="key">The key's <c>KeyboardEvent.key</c>.</param>
    internal static StoreView? TabAfterKey(StoreView current, string key)
    {
        int index = Math.Max(0, IndexOfTab(TabOf(current)));
        return key switch
        {
            "ArrowRight" => TabOrder[(index + 1) % TabOrder.Count],
            "ArrowLeft" => TabOrder[(index + TabOrder.Count - 1) % TabOrder.Count],
            "Home" => TabOrder[0],
            "End" => TabOrder[^1],
            _ => null
        };
    }

    private static int IndexOfTab(StoreView view)
    {
        for (int index = 0; index < TabOrder.Count; index++)
        {
            if (TabOrder[index] == view)
            {
                return index;
            }
        }

        return -1;
    }

    private string TabId(StoreView view) => $"{_idPrefix}-tab-{view.ToString().ToLowerInvariant()}";

    private string PanelId => _idPrefix + "-panel";

    private async Task OnTabKeyDownAsync(KeyboardEventArgs args)
    {
        if (TabAfterKey(_view, args.Key) is not { } next || next == TabOf(_view))
        {
            return;
        }

        ShowTab(next);
        StateHasChanged();
        if (_tabElements.TryGetValue(next, out ElementReference tab))
        {
            try
            {
                await tab.FocusAsync();
            }
            catch (Exception exception) when (exception is InvalidOperationException or Microsoft.JSInterop.JSException or NotSupportedException)
            {
                // No JavaScript (a test renderer): the tab still changed.
            }
        }
    }

    private void ShowTab(StoreView tab)
    {
        switch (tab)
        {
            case StoreView.Themes:
                ShowThemes();
                break;
            case StoreView.Installed:
                ShowInstalled();
                break;
            default:
                ShowHome();
                break;
        }
    }

    /// <summary>
    /// The first app the search box finds on the current tab, opened by Enter: the « Home » results
    /// or the « Installed » list. « Themes » has no page to open.
    /// </summary>
    internal StoreEntry? FirstSearchResult => _view switch
    {
        StoreView.Installed => InstalledResults.FirstOrDefault(),
        StoreView.Home when IsSearching => SearchResults.FirstOrDefault(),
        _ => null
    };

    private async Task OnSearchKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && !args.Repeat && FirstSearchResult is { } first)
        {
            await OpenDetailAsync(first);
        }
    }
}
