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
    private int _generation;
    private bool _disposed;
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
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CloseAsync();
    }

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
        int generation = ++_generation;
        FeatureSettingsPageStart start = await FeaturePages.OpenAsync(Context, Page.Id, Context.CancellationToken);
        if (generation != _generation || _disposed)
        {
            // Settings moved to another page, or closed, while this one started: it must not keep running.
            if (start.Session is { } stale)
            {
                await stale.StopAsync();
            }

            return;
        }

        if (start.Session is { } session)
        {
            _session = session;
            session.Exited += OnExited;
            if (session.HasExited)
            {
                // The OS ended it before the subscription: show the same message as a later end.
                OnExited();
            }

            return;
        }

        _message = start.IsDenied ? L["Features.PageUnavailable"] : L["Features.Page.Failed", start.Error ?? string.Empty];
        _canReopen = !start.IsDenied;
    }

    private async Task CloseAsync()
    {
        // A start still in progress is abandoned: OpenAsync stops it when it returns.
        _generation++;
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        session.Exited -= OnExited;
        try
        {
            await session.StopAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The page's process could not be stopped: say so; the task manager can still end it.
            _message = L["Features.Page.StopFailed", exception.Message];
            _canReopen = false;
        }
    }

    // The OS ended the page's process: Settings stays open and offers to start it again.
    private void OnExited() => _ = InvokeAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        _session = null;
        _message = L["Features.Page.Exited"];
        _canReopen = true;
        StateHasChanged();
    });
}
