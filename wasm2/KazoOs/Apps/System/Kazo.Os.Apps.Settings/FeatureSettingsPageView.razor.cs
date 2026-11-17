using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor.Settings;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Draws a page a feature adds to Settings ▸ Features (ADR 0249 §2). The OS starts the page's
/// process as a child of Settings when the page opens, and this view stops it when Settings leaves
/// the page or closes. The page runs in its own context: this view only places the content the OS
/// gives (<see cref="IFeatureSettingsPageSession.Content"/>).
/// </summary>
public partial class FeatureSettingsPageView : IAsyncDisposable
{
    private IFeatureSettingsPageSession? _session;
    private string? _openedId;
    private string? _message;
    private bool _canReopen;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Gets or sets the page to draw.</summary>
    [Parameter]
    [EditorRequired]
    public FeatureSettingsPageInfo Page { get; set; } = default!;

    /// <summary>Gets or sets the pages of the features, provided by the OS (ADR 0249).</summary>
    [Inject]
    private IFeatureSettingsPages FeaturePages { get; set; } = default!;

    /// <summary>Gets the Settings window's context: the page's process is its child.</summary>
    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This view must be rendered inside the Settings window.");

    /// <summary>The view's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets whether the page's process runs (tests).</summary>
    internal bool IsOpen => _session is { HasExited: false };

    /// <summary>Gets the message shown instead of the page (tests).</summary>
    internal string? Message => _message;

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (string.Equals(_openedId, Page.Id, StringComparison.Ordinal))
        {
            return;
        }

        await CloseAsync();
        _openedId = Page.Id;
        await OpenAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync();

    /// <summary>Starts the page again after its process ended (task manager, <c>kill</c>).</summary>
    internal async Task ReopenAsync()
    {
        await CloseAsync();
        await OpenAsync();
    }

    private async Task OpenAsync()
    {
        _message = null;
        _canReopen = false;
        FeatureSettingsPageStart start = await FeaturePages.OpenAsync(Context, Page.Id, Context.CancellationToken);
        if (start.Session is { } session)
        {
            _session = session;
            session.Exited += OnExited;
            return;
        }

        _message = start.IsDenied ? L["Features.PageUnavailable"] : L["Features.Page.Failed", start.Error ?? string.Empty];
        _canReopen = !start.IsDenied;
    }

    private async Task CloseAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        session.Exited -= OnExited;
        await session.StopAsync();
    }

    // The OS ended the page's process: Settings stays open and offers to start it again.
    private void OnExited() => _ = InvokeAsync(() =>
    {
        _session = null;
        _message = L["Features.Page.Exited"];
        _canReopen = true;
        StateHasChanged();
    });
}
