using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// The state of one feature on its card in Settings ▸ Features ▸ Installed features, with "Disable" or
/// "Enable" (ADR 0287 §5). Disabling keeps the feature installed: it, its entry points and the apps that
/// need it stop and no longer show, for every user, once an administrator authenticated.
/// </summary>
/// <remarks>
/// Uses only the public SDK: <see cref="IAppManagementGateway.PlanUninstallAsync"/> names, before
/// confirming, the same apps and processes a disable stops; <see cref="IAppManagementGateway.DisableFeatureAsync"/>
/// and <see cref="IAppManagementGateway.EnableFeatureAsync"/> make the change. The OS asks for the
/// administrator in its own window; this row never sees a password.
/// </remarks>
public partial class FeatureEnablementRow
{
    private KazoUninstallPlan? _pending;
    private bool _isBusy;
    private string? _message;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Gets or sets the feature shown, installed or disabled.</summary>
    [Parameter, EditorRequired]
    public KazoAppInstallInfo Feature { get; set; } = default!;

    /// <summary>Gets or sets every catalog app, to name the apps a disable takes along.</summary>
    [Parameter]
    public IReadOnlyList<KazoAppInstallInfo> Apps { get; set; } = [];

    /// <summary>Gets or sets what to do once the feature changed: the card list reads the states again.</summary>
    [Parameter]
    public EventCallback Changed { get; set; }

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This row must be rendered inside the Settings window.");

    /// <summary>The row's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Whether the feature is disabled (ADR 0287); otherwise it is enabled.</summary>
    internal bool IsDisabled => Feature.State == KazoAppInstallState.Disabled;

    /// <summary>Only an administrator is offered the action (answer 306 A); the OS still asks for their password.</summary>
    private bool IsAdministrator => KazoUser.Describe(Context).IsAdministrator;

    private string DialogTitleId => "disable-" + Feature.App.Id.Replace('.', '-');

    private string NameOf(string appId) =>
        Apps.FirstOrDefault(entry => entry.App.Id == appId)?.App.Name ?? appId;

    /// <summary>
    /// The apps of a plan other than the feature and its own entry points: those that depend on it
    /// and are disabled with it (answer 308 A). The card already lists the entry points.
    /// </summary>
    private IReadOnlyList<string> DependentsIn(KazoUninstallPlan plan) =>
        [.. plan.AppIds.Where(id => id != Feature.App.Id
            && Apps.FirstOrDefault(entry => entry.App.Id == id)?.App.ParentId != Feature.App.Id)];

    /// <summary>Opens the confirmation with what the disable will stop (ADR 0097 §5, answer 307 A).</summary>
    internal async Task AskDisableAsync()
    {
        _message = null;
        try
        {
            _pending = await Context.AppManagement.PlanUninstallAsync(Feature.App.Id, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or AppGatewayAccessDeniedException or UnauthorizedAccessException)
        {
            // The confirmation still shows, without the list it could not get.
            _pending = new KazoUninstallPlan([Feature.App.Id], []);
        }

        StateHasChanged();
    }

    private void CancelDisable()
    {
        if (!_isBusy)
        {
            _pending = null;
        }
    }

    internal async Task ConfirmDisableAsync()
    {
        KazoDisableReport? report = null;
        bool done = await RunAsync(async () => report = await Context.AppManagement.DisableFeatureAsync(Feature.App.Id, Context.CancellationToken));
        if (done)
        {
            _pending = null;
            _message = report is null ? null : DescribeStopped(report);
            StateHasChanged();
        }
    }

    internal async Task EnableAsync()
    {
        bool done = await RunAsync(async () => await Context.AppManagement.EnableFeatureAsync(Feature.App.Id, Context.CancellationToken));
        if (done)
        {
            _message = L["Features.Enabled.Message", Feature.App.Name];
            StateHasChanged();
        }
    }

    /// <summary>The result line after a disable: which processes were stopped, and which had to be forced.</summary>
    private string? DescribeStopped(KazoDisableReport report)
    {
        if (report.StoppedProcesses.Count == 0)
        {
            return L["Features.Disabled.Message", Feature.App.Name];
        }

        string stopped = string.Join(", ", report.StoppedProcesses.Select(p => L["Apps.ProcessItem", p.Name, p.Pid].Value));
        KazoRunningProcessInfo[] forced = [.. report.StoppedProcesses.Where(static p => p.Forced)];
        return forced.Length == 0
            ? L["Apps.Stopped", stopped]
            : L["Apps.StoppedAndForced", stopped, string.Join(", ", forced.Select(p => L["Apps.ProcessItem", p.Name, p.Pid].Value))];
    }

    /// <summary>Runs one change and turns its outcome into a message; returns whether it succeeded.</summary>
    private async Task<bool> RunAsync(Func<Task> change)
    {
        if (_isBusy)
        {
            return false;
        }

        _isBusy = true;
        _message = null;
        StateHasChanged();
        bool done = false;
        try
        {
            await change();
            done = true;
        }
        catch (AppGatewayAccessDeniedException)
        {
            _message = L["Apps.Error.NotAllowed"];
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _message = L["Features.Error.NoAdmin"];
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            _message = exception.Message;
            return false;
        }
        catch (IOException exception)
        {
            _message = L["Apps.Error.CannotWrite", exception.Message];
            return false;
        }
        finally
        {
            _isBusy = false;
            StateHasChanged();
        }

        if (done)
        {
            // Outside the change's try: the list's own reload never reads as a failed change.
            await Changed.InvokeAsync();
        }

        return done;
    }
}
