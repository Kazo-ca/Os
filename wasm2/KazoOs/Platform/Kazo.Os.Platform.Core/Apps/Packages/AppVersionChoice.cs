using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>
/// Picks the version of an app the Store, <c>kazopkg</c> and the update check offer (ADR 0271 §6,
/// chantier 3 of the release channels): the newest one whose SDK range accepts the SDK of this OS.
/// </summary>
/// <remarks>
/// <para>
/// A catalog keeps several versions of an app, each with the SDK it was compiled against
/// (<c>sdkCompatibility.minimumVersion</c>). An app rebuilt on a newer SDK no longer installs on an
/// older OS (an offline PWA not updated yet, a server left on an earlier version): offering the
/// newest version would only lead to <c>apps.package.sdk-incompatible</c>. The newest version that
/// runs here is offered instead, as <see cref="CatalogAppSuggestions"/> already did for a file.
/// </para>
/// <para>
/// Prereleases of an app (<c>1.0.1-beta.1</c>) are offered when the OS itself is a prerelease (it
/// reads the beta channel of the catalog, ADR 0271 §6), or when the installed version is one (a
/// user on a prerelease follows prereleases, as before). Otherwise only stable versions are, unless
/// the app has published nothing but prereleases. A version whose SDK range is unknown (an
/// <c>index.json</c> of a GitHub repository gives none) counts as compatible: the install checks
/// the manifest of the package again.
/// </para>
/// </remarks>
internal static class AppVersionChoice
{
    /// <summary>Gets whether prereleases of apps are offered on this OS and for this installed version.</summary>
    /// <param name="sdkVersion">The SDK of this OS.</param>
    /// <param name="installed">The installed version of the app, or <see langword="null"/>.</param>
    public static bool AllowsPreReleases(SemanticVersion sdkVersion, SemanticVersion? installed = null) =>
        sdkVersion.PreRelease is not null || installed?.PreRelease is not null;

    /// <summary>Gets whether an SDK range read from a catalog accepts <paramref name="sdkVersion"/>.</summary>
    /// <param name="minimum">The <c>minimumVersion</c>, or <see langword="null"/> when unknown.</param>
    /// <param name="maximum">The <c>maximumVersion</c>, or <see langword="null"/>.</param>
    /// <param name="sdkVersion">The SDK of this OS.</param>
    public static bool IsCompatible(string? minimum, string? maximum, SemanticVersion sdkVersion) =>
        minimum is null || KazoOsSdk.IsCompatible(new AppSdkCompatibilityManifest(minimum, maximum), sdkVersion);

    /// <summary>
    /// Picks the version to offer among <paramref name="versions"/>, or <see langword="null"/> when
    /// none runs on this OS.
    /// </summary>
    /// <typeparam name="T">A version of a catalog or of an index.</typeparam>
    /// <param name="versions">The published versions, in any order.</param>
    /// <param name="version">Reads the version number of an item.</param>
    /// <param name="isCompatible">Whether an item runs on this OS.</param>
    /// <param name="allowPreReleases">Whether prereleases are offered (<see cref="AllowsPreReleases"/>).</param>
    public static T? Newest<T>(
        IEnumerable<T> versions, Func<T, SemanticVersion> version, Func<T, bool> isCompatible, bool allowPreReleases)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(isCompatible);
        T[] ordered = [.. versions.OrderByDescending(version)];
        bool hasStable = ordered.Any(item => version(item).PreRelease is null);
        return ordered
            .Where(item => allowPreReleases || !hasStable || version(item).PreRelease is null)
            .FirstOrDefault(isCompatible);
    }

    /// <summary>Picks the version of a catalog app to offer on <paramref name="sdkVersion"/>.</summary>
    /// <param name="app">The catalog app.</param>
    /// <param name="sdkVersion">The SDK of this OS.</param>
    /// <param name="installed">The installed version, or <see langword="null"/>.</param>
    public static AppCatalogVersion? Newest(AppCatalogEntry app, SemanticVersion sdkVersion, SemanticVersion? installed = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return Newest(
            app.Versions,
            static item => item.Version,
            item => IsCompatible(item.MinimumSdkVersion, item.MaximumSdkVersion, sdkVersion),
            AllowsPreReleases(sdkVersion, installed));
    }

    /// <summary>
    /// The version a catalog app shows as the one installed by default: the newest that runs here,
    /// or the catalog's <see cref="AppCatalogEntry.LatestVersion"/> when none does (the Store then
    /// shows it, and the install explains why it is refused).
    /// </summary>
    /// <param name="app">The catalog app.</param>
    /// <param name="sdkVersion">The SDK of this OS.</param>
    public static SemanticVersion Offered(AppCatalogEntry app, SemanticVersion sdkVersion) =>
        Newest(app, sdkVersion)?.Version ?? app.LatestVersion;

    /// <summary>Picks the version of an index to offer on <paramref name="sdkVersion"/>.</summary>
    /// <param name="index">The versions of a repository or of a catalog app.</param>
    /// <param name="sdkVersion">The SDK of this OS.</param>
    /// <param name="installed">The installed version, or <see langword="null"/>.</param>
    public static AppPackageIndexEntry? Newest(AppPackageIndex index, SemanticVersion sdkVersion, SemanticVersion? installed = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        return Newest(
            index.Versions,
            static entry => entry.Version,
            entry => IsCompatible(entry.MinimumSdkVersion, entry.MaximumSdkVersion, sdkVersion),
            AllowsPreReleases(sdkVersion, installed));
    }

    /// <summary>
    /// The version of an index installed when none is asked: the newest that runs here, or
    /// <see cref="AppPackageIndex.Latest"/> when none does, so the install refuses it with
    /// <c>apps.package.sdk-incompatible</c> and says which SDK it needs.
    /// </summary>
    /// <param name="index">The versions of a repository or of a catalog app.</param>
    /// <param name="sdkVersion">The SDK of this OS.</param>
    public static AppPackageIndexEntry? Offered(AppPackageIndex index, SemanticVersion sdkVersion) =>
        Newest(index, sdkVersion) ?? index.Latest;
}
