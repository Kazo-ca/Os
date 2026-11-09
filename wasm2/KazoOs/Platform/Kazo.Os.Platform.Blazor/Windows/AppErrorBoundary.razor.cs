using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Processes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Keeps an app's failure inside its own window (ADR 0238). An exception thrown while the app's
/// components render, run a lifecycle method (<c>OnAfterRenderAsync</c> included) or handle an
/// event no longer reaches the host's boundary, which would stop the whole OS: this boundary,
/// placed around the app's entry point, shows "« App » a rencontré un problème" in the window
/// instead, with <em>Réessayer</em> (renders the app's components again) and <em>Fermer
/// l'application</em> (stops its process, <see cref="ProcessExitReason.Fault"/>).
/// </summary>
/// <remarks>
/// The failure is written to the OS log with the app's id, so <c>/var/log/syslog</c> and the
/// browser console keep the full exception. What the window shows is the exception's type and
/// message only.
/// </remarks>
public sealed partial class AppErrorBoundary
{
    /// <summary>Gets or sets the texts, in the session language (ADR 0104).</summary>
    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    /// <summary>Gets or sets the services, to find the optional logger and lifecycle orchestrator.</summary>
    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>Gets or sets the app's name shown in the message (its localized manifest name).</summary>
    [Parameter]
    public string? AppName { get; set; }

    /// <summary>
    /// Gets or sets the execution context of the app instance the window belongs to. With it, the
    /// panel offers to close the app; without it (a test, a surface with no process), only to retry.
    /// </summary>
    [Parameter]
    public IAppExecutionContext? AppContext { get; set; }

    private string TitleId { get; } = $"app-failure-title-{Guid.NewGuid():N}";

    private string DisplayName => string.IsNullOrWhiteSpace(AppName) ? AppContext?.Manifest.Id ?? "?" : AppName;

    private bool CanClose => AppContext is not null && Services.GetService(typeof(AppLifecycleOrchestrator)) is not null;

    private string ErrorSummary => CurrentException is null
        ? string.Empty
        : $"{CurrentException.GetType().Name}: {CurrentException.Message}";

    /// <inheritdoc />
    protected override Task OnErrorAsync(Exception exception)
    {
        if (Services.GetService(typeof(ILogger<AppErrorBoundary>)) is ILogger<AppErrorBoundary> logger)
        {
            logger.LogError(
                exception,
                "apps: {AppId} failed in its window, the rest of the OS keeps running",
                AppContext?.Manifest.Id ?? DisplayName);
        }

        return Task.CompletedTask;
    }

    private Task RetryAsync()
    {
        // The app's components are created again, inside the same process and window.
        Recover();
        return Task.CompletedTask;
    }

    private async Task CloseAsync()
    {
        if (AppContext is null || Services.GetService(typeof(AppLifecycleOrchestrator)) is not AppLifecycleOrchestrator orchestrator)
        {
            return;
        }

        await orchestrator.StopAsync(AppInstanceId.FromGuid(AppContext.InstanceId), ProcessExitReason.Fault);
    }
}
