using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Store;

/// <summary>
/// An app's screenshots on its Store page and their viewer (store-ux series, work item 2). Public
/// only because a Razor component cannot be internal; it is not meant for other apps.
/// </summary>
/// <remarks>
/// <para>
/// The viewer stays inside the Store window: it covers the window, not the screen, like any other
/// part of an app. The window is the app's whole world in Kazo OS; a real full-screen image would
/// hide the taskbar and the other windows for a screenshot.
/// </para>
/// <para>
/// No JavaScript: the keys, the swipe and the focus go through Blazor's own events and
/// <c>ElementReference.FocusAsync()</c>. The state itself is
/// <see cref="StoreScreenshotViewerState"/>, which the tests drive without a browser.
/// </para>
/// </remarks>
public sealed partial class StoreScreenshots : IDisposable
{
    private StoreScreenshotViewerState _state = StoreScreenshotViewerState.Closed(0);
    private IReadOnlyList<string> _shownUris = [];
    private ElementReference[] _thumbnails = [];
    private ElementReference _dialog;
    private ElementReference _closeButton;
    private ElementReference _nextButton;
    private FocusTarget _pendingFocus;
    private int _returnIndex;
    private (double X, double Y)? _pointerStart;
    private IStringLocalizer<Strings>? _text;
    private bool _swiped;

    /// <summary>Where the focus goes once the viewer has been drawn again.</summary>
    private enum FocusTarget
    {
        None,
        Dialog,
        Close,
        Next,
        Thumbnail,
    }

    /// <summary>Gets or sets the screenshots' addresses, in the catalog's order.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<string> Uris { get; set; } = [];

    /// <summary>Gets or sets the app's name, read in the buttons' and the viewer's labels.</summary>
    [Parameter]
    [EditorRequired]
    public string AppName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what runs when the viewer opens (<see langword="true"/>) or closes: the Store
    /// window draws itself again, so the OS's Back knows it has a viewer to close.
    /// </summary>
    [Parameter]
    public EventCallback<bool> ViewerOpenChanged { get; set; }

    /// <summary>The Store's texts in the language of the Store process (ADR 0104).</summary>
    private IStringLocalizer<Strings> Text => _text ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets a value indicating whether the viewer shows a screenshot.</summary>
    internal bool IsViewerOpen => _state.IsOpen;

    /// <summary>Gets the viewer's state, for the tests.</summary>
    internal StoreScreenshotViewerState State => _state;

    /// <summary>Opens the viewer on a screenshot, as a click on its thumbnail does.</summary>
    /// <param name="index">The screenshot, from 0.</param>
    internal async Task OpenAsync(int index)
    {
        StoreScreenshotViewerState opened = _state.Open(index);
        if (!opened.IsOpen)
        {
            return;
        }

        bool wasOpen = _state.IsOpen;
        _state = opened;
        _returnIndex = index;
        _pendingFocus = FocusTarget.Close;
        StateHasChanged();
        await NotifyIfChangedAsync(wasOpen);
    }

    /// <summary>Closes the viewer and gives the focus back to the thumbnail it was opened from.</summary>
    internal async Task CloseAsync()
    {
        if (!_state.IsOpen)
        {
            return;
        }

        // The focus returns to the screenshot last shown, so the keyboard user finds their place
        // in the strip even after moving through the viewer.
        _returnIndex = _state.Index ?? _returnIndex;
        _state = _state.Close();
        _pendingFocus = FocusTarget.Thumbnail;

        // Drawn here and not only after an event: the Store window's Back closes the viewer too.
        StateHasChanged();
        await NotifyIfChangedAsync(wasOpen: true);
    }

    /// <summary>Applies a key pressed in the viewer.</summary>
    /// <param name="args">The key.</param>
    internal async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        // Alt+← is the OS's Back, which closes the viewer through the Store window; a chord is
        // never a move inside the viewer.
        if (args.AltKey || args.CtrlKey || args.MetaKey)
        {
            return;
        }

        if (args.Key == "Escape")
        {
            await CloseAsync();
            return;
        }

        Move(_state.Key(args.Key));
    }

    /// <summary>Shows the previous screenshot.</summary>
    internal Task PreviousAsync()
    {
        Move(_state.Previous());
        return Task.CompletedTask;
    }

    /// <summary>Shows the next screenshot.</summary>
    internal Task NextAsync()
    {
        Move(_state.Next());
        return Task.CompletedTask;
    }

    /// <summary>
    /// The app's page went away (Back, another app): the viewer counts as closed, so the Store
    /// window, which may still hold this component, never asks it to close again.
    /// </summary>
    public void Dispose() => _state = _state.Close();

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        // Another app's page (or a catalog refresh that changed the list) closes the viewer: the
        // index shown would point at another app's screenshot.
        if (!_shownUris.SequenceEqual(Uris, StringComparer.Ordinal))
        {
            _shownUris = [.. Uris];
            _state = StoreScreenshotViewerState.Closed(Uris.Count);
            _thumbnails = new ElementReference[Uris.Count];
            _pendingFocus = FocusTarget.None;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        FocusTarget target = _pendingFocus;
        _pendingFocus = FocusTarget.None;
        ElementReference? element = target switch
        {
            FocusTarget.Dialog => _dialog,
            FocusTarget.Close => _closeButton,
            FocusTarget.Next => _nextButton,
            FocusTarget.Thumbnail when _returnIndex >= 0 && _returnIndex < _thumbnails.Length => _thumbnails[_returnIndex],
            _ => null,
        };

        if (element is { } focus && focus.Context is not null)
        {
            try
            {
                await focus.FocusAsync();
            }
            catch (InvalidOperationException)
            {
                // The element left the page between the render and this call (the user went back
                // to the list): there is nothing left to focus.
            }
        }
    }

    /// <summary>Moves to another screenshot, keeping the focus inside the viewer.</summary>
    private void Move(StoreScreenshotViewerState next)
    {
        if (next == _state)
        {
            return;
        }

        _state = next;

        // The button just pressed is disabled on the first or the last screenshot, and a disabled
        // button drops the focus out of the viewer: the viewer itself takes it so the keys keep
        // working.
        if (!_state.HasPrevious || !_state.HasNext)
        {
            _pendingFocus = FocusTarget.Dialog;
        }

        StateHasChanged();
    }

    private async Task NotifyIfChangedAsync(bool wasOpen)
    {
        if (wasOpen != _state.IsOpen && ViewerOpenChanged.HasDelegate)
        {
            await ViewerOpenChanged.InvokeAsync(_state.IsOpen);
        }
    }

    private Task FocusFirstAsync()
    {
        _pendingFocus = FocusTarget.Close;
        return Task.CompletedTask;
    }

    private Task FocusLastAsync()
    {
        _pendingFocus = _state.HasNext ? FocusTarget.Next : FocusTarget.Close;
        return Task.CompletedTask;
    }

    private Task OnStageClickAsync()
    {
        // A swipe ends with a click on what is under the finger: that click is not « close ».
        if (_swiped)
        {
            _swiped = false;
            return Task.CompletedTask;
        }

        return CloseAsync();
    }

    private void OnPointerDown(PointerEventArgs args)
    {
        _pointerStart = (args.ClientX, args.ClientY);
        _swiped = false;
    }

    private Task OnPointerUpAsync(PointerEventArgs args)
    {
        if (_pointerStart is not { } start)
        {
            return Task.CompletedTask;
        }

        _pointerStart = null;
        double deltaX = args.ClientX - start.X;
        double deltaY = args.ClientY - start.Y;
        if (StoreScreenshotViewerState.IsSwipe(deltaX, deltaY))
        {
            _swiped = true;
            Move(_state.Swipe(deltaX, deltaY));
        }

        return Task.CompletedTask;
    }

    private void OnPointerCancel() => _pointerStart = null;
}
