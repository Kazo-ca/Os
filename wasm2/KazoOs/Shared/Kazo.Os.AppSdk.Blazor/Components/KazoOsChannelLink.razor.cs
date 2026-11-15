using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.AppSdk.Blazor.Components;

/// <summary>
/// The link between the stable and the beta release channels of Kazo OS (ADR 0271 §3 and §6).
/// The channel comes from the version: a version with a prerelease suffix (<c>3.4.0-preview.1</c>)
/// is a beta. Each channel lives on its own origin, so the two never share a database or a cache.
/// </summary>
public partial class KazoOsChannelLink
{
    /// <summary>The address of the stable site, deployed from <c>main</c>.</summary>
    public const string StableSiteAddress = "https://os.kazo.ca/";

    /// <summary>The address of the beta site, deployed from <c>beta</c> (ADR 0271 §3, Q1).</summary>
    public const string BetaSiteAddress = "https://os-beta.kazo.ca/";

    /// <summary>The app the link belongs to, cascaded by the window host: its language applies (ADR 0104).</summary>
    [CascadingParameter]
    private IAppExecutionContext? AppContext { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    /// <summary>Gets or sets the version of the running OS; <see cref="KazoOsSdk.Version"/> by default.</summary>
    [Parameter]
    public string Version { get; set; } = KazoOsSdk.Version;

    /// <summary>
    /// Gets whether <paramref name="version"/> belongs to the beta channel: a valid SemVer version
    /// with a prerelease suffix. Text that is not SemVer is not a beta.
    /// </summary>
    /// <param name="version">The version text, e.g. <c>3.4.0-preview.1</c>.</param>
    /// <returns><see langword="true"/> for a beta version.</returns>
    public static bool IsBeta(string? version) =>
        version is not null && SemanticVersion.TryParse(version, out SemanticVersion parsed) && parsed.PreRelease is not null;

    /// <summary>
    /// Gets the site the link leads to, or <see langword="null"/> when there is none: a beta leads
    /// back to the stable site wherever it runs; the stable version offers the beta only on the
    /// stable site itself, so a server or a local build never sends its users to the public beta.
    /// </summary>
    /// <param name="running">The version of the running OS.</param>
    /// <param name="page">The address of the current page.</param>
    /// <returns>The address of the other channel's site, or <see langword="null"/>.</returns>
    public static Uri? GetSwitchTarget(SemanticVersion running, Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (running.PreRelease is not null)
        {
            return new Uri(StableSiteAddress);
        }

        // Compare the actual host, never a substring: a lookalike domain must not get the link.
        return page.IsAbsoluteUri && string.Equals(page.Host, new Uri(StableSiteAddress).Host, StringComparison.OrdinalIgnoreCase)
            ? new Uri(BetaSiteAddress)
            : null;
    }

    private Uri? Target => SemanticVersion.TryParse(Version, out SemanticVersion running)
        ? GetSwitchTarget(running, new Uri(Navigation.Uri))
        : null;

    private string Text => IsBeta(Version) ? L["Version.BackToStable"] : L["Version.TryBeta"];

    /// <summary>The texts in the app's language inside an app, otherwise the host's (as <see cref="KazoOsVersionLabel"/>).</summary>
    private IStringLocalizer<Strings> L => AppContext is { } context
        ? context.GetLocalizer<Strings>()
        : Services.GetService<IStringLocalizer<Strings>>() ?? KazoLocalization.GetLocalizer<Strings>();
}
