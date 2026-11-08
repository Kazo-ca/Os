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

    /// <summary>« build 1234 », or « build dev » for a local build (no number from the CI).</summary>
    private string BuildText =>
        (_monitor?.RunningBuild ?? HostBuildNumber.Read(System.Reflection.Assembly.GetEntryAssembly())) is { } build
            ? L["Project.Build", build]
            : L["Project.BuildLocal"];

    /// <summary>
    /// The text and style of the status badge, or <see langword="null"/> when the host has no
    /// release monitor (a server prerender, a host without <c>version.json</c>).
    /// </summary>
    private (string Text, string CssClass)? Status => _monitor is null ? null : Describe(_monitor);

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

    private (string Text, string CssClass) Describe(HostReleaseMonitor monitor) => monitor.Status switch
    {
        HostReleaseStatus.UpToDate => (L["Project.Status.UpToDate"], "is-up-to-date"),
        HostReleaseStatus.UpdateAvailable => (DescribeUpdate(monitor.AvailableVersion, monitor.AvailableBuild), "is-outdated"),
        HostReleaseStatus.Unreachable => (L["Project.Status.Unreachable"], "is-unreachable"),
        _ => (L["Project.Status.Checking"], "is-checking"),
    };

    private string DescribeUpdate(string? version, int? build) => (version, build) switch
    {
        ({ } v, { } b) => L["Project.Status.OutdatedVersionBuild", v, b],
        ({ } v, null) => L["Project.Status.OutdatedVersion", v],
        (null, { } b) => L["Project.Status.OutdatedBuild", b],
        _ => L["Project.Status.Outdated"],
    };
}
