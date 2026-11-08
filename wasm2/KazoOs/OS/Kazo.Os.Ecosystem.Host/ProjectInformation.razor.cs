using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The footer of the sign-in and recovery screens (ADR 0187 §7): the version of Kazo OS, its
/// build number and whether it is the version the origin serves now (ADR 0236), the credits, and
/// the notice that the original version moved.
/// </summary>
public partial class ProjectInformation : IDisposable
{
    private HostReleaseMonitor? _monitor;

    /// <summary>Shows the original-version notice on authentication pages, on the public domain only.</summary>
    [Parameter] public bool ShowLegacyNotice { get; set; } = true;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    // Compare the actual host, never a substring: a query string or lookalike domain is insufficient.
    private bool IsPublicDomain => string.Equals(new Uri(Navigation.Uri).Host, "os.kazo.ca", StringComparison.OrdinalIgnoreCase);

    /// <summary>The running build: the monitor's, or the one written on the entry assembly without a monitor.</summary>
    private int? BuildNumber => _monitor?.RunningBuild ?? KazoOsSdk.BuildNumber;

    /// <summary>
    /// The badge next to the version: none when the host has no release monitor (a server
    /// prerender, a host without <c>version.json</c>).
    /// </summary>
    private KazoOsVersionStatus Status => _monitor?.Status switch
    {
        null => KazoOsVersionStatus.None,
        HostReleaseStatus.UpToDate => KazoOsVersionStatus.UpToDate,
        HostReleaseStatus.UpdateAvailable => KazoOsVersionStatus.Outdated,
        HostReleaseStatus.Unreachable => KazoOsVersionStatus.Unreachable,
        _ => KazoOsVersionStatus.Checking,
    };

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        // Only the browser host registers it (ADR 0174).
        _monitor = Services.GetService<HostReleaseMonitor>();
        if (_monitor is not null)
        {
            _monitor.StatusChanged += OnStatusChanged;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.StatusChanged -= OnStatusChanged;
        }

        GC.SuppressFinalize(this);
    }

    private void OnStatusChanged() => InvokeAsync(StateHasChanged);
}
