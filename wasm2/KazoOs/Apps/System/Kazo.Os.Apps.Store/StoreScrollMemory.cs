using Kazo.Os.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.Store;

/// <summary>
/// Puts the Store's page back at the top when the view changes, and back where it was when the
/// user returns from an app's page to the list it was opened from.
/// </summary>
/// <remarks>
/// <para>
/// Every view of the Store (home, a category, the search results, « Installed », « Themes », an app's
/// page) is drawn in the same scrolling <c>&lt;main class="store-body"&gt;</c>, which keeps its scroll
/// position: without this component an app's page opened from a scrolled list opened in the middle
/// of its description, and on a phone at the very bottom.
/// </para>
/// <para>
/// The component is drawn as the first child of that body: a hidden, empty marker whose parent the
/// script <c>wwwroot/store-scroll.js</c> scrolls. The script is the only part C# cannot do (a scroll
/// position is not in the render tree). Without JavaScript (a test renderer) nothing happens.
/// </para>
/// </remarks>
internal sealed class StoreScrollMemory : KazoComponentBase, IAsyncDisposable
{
    private const string DetailPrefix = "detail:";

    private ElementReference _marker;
    private IJSObjectReference? _module;
    private string? _shownKey;
    private bool _disposed;

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    /// <summary>The view on screen (see <see cref="ViewKey"/>).</summary>
    [Parameter]
    [EditorRequired]
    public string View { get; set; } = string.Empty;

    /// <summary>The key of a view: one per page the user can scroll on its own.</summary>
    /// <param name="view">The Store's page.</param>
    /// <param name="appId">The app whose page is open.</param>
    /// <param name="categoryId">The category open on the home page.</param>
    /// <param name="searching">Whether the home page shows search results.</param>
    public static string ViewKey(StoreView view, string? appId, string? categoryId, bool searching) => view switch
    {
        StoreView.Detail => DetailPrefix + appId,
        StoreView.Installed => "installed",
        StoreView.Themes => "themes",
        _ when searching => "search",
        _ when categoryId is not null => "category:" + categoryId,
        _ => "home"
    };

    /// <summary>
    /// Whether the new view comes back where it was left: only a list the user returns to from an
    /// app's page. Any other change of view starts at the top.
    /// </summary>
    /// <param name="previousKey">The view shown before, or <see langword="null"/> on the first render.</param>
    /// <param name="key">The view shown now.</param>
    public static bool ShouldRestore(string? previousKey, string key) =>
        previousKey is not null
        && previousKey.StartsWith(DetailPrefix, StringComparison.Ordinal)
        && !key.StartsWith(DetailPrefix, StringComparison.Ordinal);

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "store-scroll-memory");
        builder.AddAttribute(2, "hidden", true);
        builder.AddAttribute(3, "aria-hidden", "true");
        builder.AddElementReferenceCapture(4, reference => _marker = reference);
        builder.CloseElement();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || View == _shownKey)
        {
            return;
        }

        bool restore = ShouldRestore(_shownKey, View);
        _shownKey = View;
        try
        {
            // The path is written out: the SDK boundary test (ADR 0055 §3) reads it in the source.
            _module ??= await JavaScript.InvokeAsync<IJSObjectReference>("import", "./_content/Kazo.Os.Apps.Store/store-scroll.js");
            await _module.InvokeVoidAsync("showView", _marker, View, restore);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
            // No JavaScript (a test renderer) or the window is closing: the page simply keeps its position.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.DisposeAsync();
        }
        catch (Exception exception) when (exception is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // The page is going away with the window.
        }
    }
}
