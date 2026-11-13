using Kazo.Os.AppSdk.Blazor;

namespace Kazo.Os.Apps.Store;

/// <summary>
/// The mouse's Back button, Alt+← and the phone's Back in the Kazo OS Store (ADR 0220 §4): an app's page goes back
/// to the list it was opened from, a category back to the whole catalog, and an open screenshot
/// viewer closes first.
/// </summary>
public partial class StoreWindow : IAppBackHandler
{
    private bool _lastCanNavigateBack;
    private StoreScreenshots? _screenshots;

    /// <inheritdoc />
    public bool CanNavigateBack => IsScreenshotViewerOpen || _view == StoreView.Detail || (_view == StoreView.Home && _categoryId is not null);

    /// <inheritdoc />
    public event Action? CanNavigateBackChanged;

    /// <inheritdoc />
    public async ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
    {
        if (IsScreenshotViewerOpen)
        {
            // The screenshot viewer covers the app's page: Back closes it first, as Escape does.
            // The OS may call from outside the renderer's dispatcher, hence InvokeAsync.
            StoreScreenshots viewer = _screenshots!;
            await InvokeAsync(viewer.CloseAsync);
        }
        else if (_view == StoreView.Detail)
        {
            CloseDetail();
        }
        else if (_view == StoreView.Home && _categoryId is not null)
        {
            _categoryId = null;
        }
        else
        {
            return AppBackResult.NotHandled;
        }

        await InvokeAsync(StateHasChanged);
        return AppBackResult.Handled;
    }

    /// <summary>Gets a value indicating whether an app's screenshot is shown at the size of the window.</summary>
    private bool IsScreenshotViewerOpen => _view == StoreView.Detail && _screenshots?.IsViewerOpen == true;

    /// <summary>
    /// The viewer opened or closed: the window draws itself again, which tells the OS whether Back
    /// has something to close (<see cref="OnAppAfterRenderAsync"/>).
    /// </summary>
    private void OnScreenshotViewerChanged(bool isOpen) => StateHasChanged();

    /// <inheritdoc />
    protected override Task OnAppAfterRenderAsync(bool firstRender)
    {
        if (CanNavigateBack != _lastCanNavigateBack)
        {
            _lastCanNavigateBack = CanNavigateBack;
            CanNavigateBackChanged?.Invoke();
        }

        return base.OnAppAfterRenderAsync(firstRender);
    }
}
