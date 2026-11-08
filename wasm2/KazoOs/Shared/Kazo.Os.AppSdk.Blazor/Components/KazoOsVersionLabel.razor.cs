using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.AppSdk.Blazor.Components;

/// <summary>
/// Shows the version of Kazo OS, its build number and, optionally, whether it is up to date
/// (ADR 0236). Every screen that names the version uses this component, so the wording and the
/// look live in one place.
/// </summary>
public partial class KazoOsVersionLabel
{
    /// <summary>The app the label belongs to, cascaded by the window host: its language applies (ADR 0104).</summary>
    [CascadingParameter]
    private IAppExecutionContext? AppContext { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>Gets or sets the version shown; <see cref="KazoOsSdk.Version"/> by default.</summary>
    [Parameter]
    public string Version { get; set; } = KazoOsSdk.Version;

    /// <summary>Gets or sets the build number shown; <see cref="KazoOsSdk.BuildNumber"/> by default, « dev » when <see langword="null"/>.</summary>
    [Parameter]
    public int? BuildNumber { get; set; } = KazoOsSdk.BuildNumber;

    /// <summary>Gets or sets the badge; <see cref="KazoOsVersionStatus.None"/> (no badge) by default.</summary>
    [Parameter]
    public KazoOsVersionStatus Status { get; set; }

    /// <summary>Gets or sets the version the origin serves, when <see cref="Status"/> is <see cref="KazoOsVersionStatus.Outdated"/> because of it.</summary>
    [Parameter]
    public string? AvailableVersion { get; set; }

    /// <summary>Gets or sets the build number the origin serves, when <see cref="Status"/> is <see cref="KazoOsVersionStatus.Outdated"/>.</summary>
    [Parameter]
    public int? AvailableBuild { get; set; }

    /// <summary>
    /// The texts in the app's language inside an app; on the OS's own screens, in the language the
    /// host binds to its localizers (the system language before sign-in); otherwise the ambient one.
    /// </summary>
    private IStringLocalizer<Strings> L => AppContext is { } context
        ? context.GetLocalizer<Strings>()
        : Services.GetService<IStringLocalizer<Strings>>() ?? KazoLocalization.GetLocalizer<Strings>();

    private string BuildText => BuildNumber is { } build ? L["Version.Build", build] : L["Version.BuildLocal"];

    private string StatusClass => Status switch
    {
        KazoOsVersionStatus.UpToDate => "is-up-to-date",
        KazoOsVersionStatus.Outdated => "is-outdated",
        KazoOsVersionStatus.Unreachable => "is-unreachable",
        _ => "is-checking",
    };

    private string StatusText => Status switch
    {
        KazoOsVersionStatus.UpToDate => L["Version.UpToDate"],
        KazoOsVersionStatus.Unreachable => L["Version.Unreachable"],
        KazoOsVersionStatus.Outdated => (AvailableVersion, AvailableBuild) switch
        {
            ({ } v, { } b) => L["Version.OutdatedVersionBuild", v, b],
            ({ } v, null) => L["Version.OutdatedVersion", v],
            (null, { } b) => L["Version.OutdatedBuild", b],
            _ => L["Version.Outdated"],
        },
        _ => L["Version.Checking"],
    };
}
