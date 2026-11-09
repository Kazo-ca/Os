using Kazo.Os.App.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The body of the error page (ADR 0240): a summary of the failure (module, source, type, message,
/// time, correlation ID), its technical details folded under « Détails techniques », and the
/// « Copier les détails » and « Recharger » buttons. Everything shown was masked by
/// <see cref="Kazo.Os.Platform.Core.Diagnostics.SensitiveTextRedactor"/> when the failure was described.
/// </summary>
public partial class HostFailurePanel : IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.Ecosystem.Host/HostFailurePanel.razor.js";

    private IJSObjectReference? _module;

    /// <summary>Gets or sets the failure shown; only the « recorded » sentence when <see langword="null"/>.</summary>
    [Parameter] public HostFailureDetails? Failure { get; set; }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>The result of the last copy, announced to screen readers.</summary>
    private string? CopyStatus { get; set; }

    /// <summary>
    /// Copies <see cref="HostFailureDetails.ToReportText"/> to the real clipboard through the
    /// component's own module: the OS clipboard services may be the part that failed.
    /// </summary>
    private async Task CopyAsync()
    {
        if (Failure is null)
        {
            return;
        }

        string version = KazoOsSdk.BuildNumber is int build
            ? $"{KazoOsSdk.Version} (build {build})"
            : KazoOsSdk.Version;
        bool copied;
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            copied = await _module.InvokeAsync<bool>("copyText", Failure.ToReportText(version));
        }
        catch (JSException)
        {
            copied = false;
        }

        CopyStatus = copied ? L["Failure.Copied"] : L["Failure.CopyFailed"];
    }

    /// <summary>Reloads the page, like F5: the OS starts again from its sign-in screen.</summary>
    private void Reload() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away; nothing to release.
            }
        }

        GC.SuppressFinalize(this);
    }
}
