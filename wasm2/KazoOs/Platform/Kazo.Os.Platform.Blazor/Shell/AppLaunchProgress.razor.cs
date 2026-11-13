using Kazo.Os.Platform.Core.AppLinks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// The progress window of a direct link <c>?autolaunch=&lt;appId&gt;</c> (ADR 0246 §3): the app's
/// name and publisher read in the official catalog, what the OS is doing, a progress bar and
/// « Annuler ». It shows <see cref="AppLaunchLinkRunner.Current"/> and closes when the runner is done.
/// Hosts without the runner never show it.
/// </summary>
public partial class AppLaunchProgress : ComponentBase, IDisposable
{
    private AppLaunchLinkRunner? _runner;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _runner = Services.GetService<AppLaunchLinkRunner>();
        if (_runner is not null)
        {
            _runner.Changed += OnChanged;
        }
    }

    private string Title(AppLaunchLinkOperation operation) => operation.Step == AppLaunchLinkStep.Launching
        ? L["AppLink.Progress.OpeningTitle", operation.Name]
        : L["AppLink.Progress.Title", operation.Name];

    private string StepText(AppLaunchLinkOperation operation) => operation.Step switch
    {
        AppLaunchLinkStep.Looking => L["AppLink.Progress.Looking"],
        AppLaunchLinkStep.Installing when operation.Version is { } version => L["AppLink.Progress.InstallingVersion", version],
        AppLaunchLinkStep.Installing => L["AppLink.Progress.Installing"],
        _ => L["AppLink.Progress.Launching"],
    };

    // The runner reports from its own task: re-render on the renderer's thread.
    private void OnChanged() => InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_runner is not null)
        {
            _runner.Changed -= OnChanged;
        }
    }
}
