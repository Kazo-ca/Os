using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.Store;

/// <summary>
/// An app's page in the Kazo OS Store (ADR 0266 §4, store-ux work item 3), taken out of
/// <see cref="StoreWindow"/>. Public only because a Razor component cannot be internal; it is not
/// meant for other apps.
/// </summary>
/// <remarks>
/// <para>
/// The page has no state of its own beyond what it shows: the app, the description and every
/// action stay in the window, which the page reaches through <see cref="Store"/> (the window's
/// members it uses are internal). It only remembers whether its header is on screen and whether
/// the capabilities show their technical identifiers.
/// </para>
/// <para>
/// <c>StoreAppPage.razor.js</c> tells the page when its header scrolls out from under the sticky
/// bar (the bar then shows the app's icon, name and main button), gives the facts panel the height
/// of the scrolling body, and reads the address the OS is served from for the « Try it » link.
/// Without JavaScript (a test renderer) the header simply counts as always visible.
/// </para>
/// </remarks>
public sealed partial class StoreAppPage : IAsyncDisposable
{
    private readonly string _idPrefix = "store-page-" + Guid.NewGuid().ToString("N")[..8];
    private ElementReference _head;
    private ElementReference _bar;
    private StoreScreenshots? _screenshots;
    private IJSObjectReference? _module;
    private IJSObjectReference? _watch;
    private DotNetObjectReference<StoreAppPage>? _self;
    private string? _shownAppId;
    private bool _headVisible = true;
    private bool _showIdentifiers;
    private bool _disposed;

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    /// <summary>Gets or sets the Store window whose selected app the page shows and whose actions it runs.</summary>
    [Parameter]
    [EditorRequired]
    public StoreWindow Store { get; set; } = default!;

    /// <summary>Gets whether the screenshot viewer covers the page (the OS's Back closes it first).</summary>
    internal bool IsViewerOpen => _screenshots?.IsViewerOpen == true;

    /// <summary>Closes the screenshot viewer, as Escape does.</summary>
    internal Task CloseViewerAsync() => _screenshots?.CloseAsync() ?? Task.CompletedTask;

    private IStringLocalizer<Strings> L => Store.Text;

    private CultureInfo Culture => Store.TextCulture;

    /// <summary>The header came back on screen or left it, under the sticky bar.</summary>
    /// <param name="visible">Whether some of the header is visible.</param>
    [JSInvokable]
    public void OnHeadVisibilityChanged(bool visible)
    {
        if (visible == _headVisible)
        {
            return;
        }

        _headVisible = visible;
        _ = InvokeAsync(StateHasChanged);
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        // Another app's page starts with its header shown and its identifiers folded.
        string? appId = Store.SelectedEntry?.AppId;
        if (appId != _shownAppId)
        {
            _shownAppId = appId;
            _headVisible = true;
            _showIdentifiers = false;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }

        try
        {
            // The path is written out: the SDK boundary test (ADR 0055 §3) reads it in the source.
            _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./_content/Kazo.Os.Apps.Store/StoreAppPage.razor.js");
            _self = DotNetObjectReference.Create(this);
            _watch = await _module.InvokeAsync<IJSObjectReference>("watch", _head, _bar, _self);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
            // No JavaScript (a test renderer) or the window is closing: the header stays « visible ».
        }
    }

    private void ToggleIdentifiers() => _showIdentifiers = !_showIdentifiers;

    private string IdOf(string part) => $"{_idPrefix}-{part}";

    /// <summary>Copies the « Try it » link of ADR 0258 for this OS's own address.</summary>
    private async Task CopyTryLinkAsync(StoreEntry entry)
    {
        string? address = null;
        try
        {
            if (_module is not null)
            {
                address = await _module.InvokeAsync<string>("pageAddress");
            }
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException or TaskCanceledException)
        {
            address = null;
        }

        await Store.CopyTryLinkAsync(entry, address);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_watch is not null)
            {
                await _watch.InvokeVoidAsync("dispose");
                await _watch.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is JSDisconnectedException or JSException or TaskCanceledException or ObjectDisposedException)
        {
            // The page is going away with the window.
        }

        _self?.Dispose();
    }
}
