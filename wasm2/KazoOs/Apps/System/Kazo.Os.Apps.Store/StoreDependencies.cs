using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;

namespace Kazo.Os.Apps.Store;

/// <summary>Where one dependency of an app stands, as its page says it (ADR 0248 §2).</summary>
internal enum StoreDependencyState
{
    /// <summary>Installed in a version the app accepts.</summary>
    Installed,

    /// <summary>Required and absent: installed with the app, in the same consent.</summary>
    WithThisApp,

    /// <summary>Installed, but in a version out of the app's range: the installation brings one in range, or stops.</summary>
    OutOfRange,

    /// <summary>Optional and absent: the app runs without it, and can ask for it later.</summary>
    Optional,
}

/// <summary>One dependency of an app, for its page.</summary>
/// <param name="AppId">The app or feature needed.</param>
/// <param name="Name">Its name in a catalog, else its identifier.</param>
/// <param name="Range">The versions accepted, such as <c>&gt;= 1.0.0</c>.</param>
/// <param name="Optional">Whether the app runs without it.</param>
/// <param name="State">Where it stands.</param>
/// <param name="InstalledVersion">The version installed, or <see langword="null"/>.</param>
/// <param name="IsFeature">Whether it is a feature (ADR 0260): installed for the whole machine, by an administrator.</param>
/// <param name="DangerousPermissions">
/// For a feature to install, the dangerous capabilities its catalog entry declares: the set its
/// installation approves (ADR 0260 §3, answer 268 A). Empty otherwise.
/// </param>
internal sealed record StoreDependency(
    string AppId,
    string Name,
    string Range,
    bool Optional,
    StoreDependencyState State,
    string? InstalledVersion,
    bool IsFeature,
    IReadOnlyList<string> DangerousPermissions);

/// <summary>
/// The dependencies of an app as its page shows them (ADR 0248 §2, §5): read from the catalog, which
/// copies them from the manifest, and compared with what is installed. Pure functions, so the tests
/// read them without a browser.
/// </summary>
internal static class StoreDependencies
{
    /// <summary>Describes each dependency: required ones first, then by name.</summary>
    /// <param name="dependencies">The <c>dependencies</c> of the version shown.</param>
    /// <param name="find">The Store's entry of an app, or <see langword="null"/> when no catalog lists it.</param>
    /// <param name="running">The apps of the running OS (built-in ones included), with their version.</param>
    internal static IReadOnlyList<StoreDependency> Describe(
        IEnumerable<KazoPackageDependency> dependencies,
        Func<string, StoreEntry?> find,
        IReadOnlyDictionary<string, string> running)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(running);
        return [.. dependencies
            .Where(static dependency => !string.IsNullOrWhiteSpace(dependency.AppId))
            .DistinctBy(static dependency => dependency.AppId, StringComparer.Ordinal)
            .Select(dependency => Describe(dependency, find(dependency.AppId), running))
            .OrderBy(static dependency => dependency.Optional)
            .ThenBy(static dependency => dependency.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Gets whether a version satisfies a dependency's range, both bounds included (ADR 0005).</summary>
    /// <param name="dependency">The dependency.</param>
    /// <param name="version">A version, as text.</param>
    internal static bool InRange(KazoPackageDependency dependency, string version)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (!SemanticVersion.TryParse(version, out SemanticVersion current)
            || !SemanticVersion.TryParse(dependency.MinimumVersion, out SemanticVersion minimum)
            || current.CompareTo(minimum) < 0)
        {
            return false;
        }

        return dependency.MaximumVersion is null
            || (SemanticVersion.TryParse(dependency.MaximumVersion, out SemanticVersion maximum) && current.CompareTo(maximum) <= 0);
    }

    /// <summary>Writes a range as the page shows it: <c>&gt;= 1.0.0</c>, or <c>1.0.0 – 1.9.0</c>.</summary>
    /// <param name="dependency">The dependency.</param>
    internal static string RangeText(KazoPackageDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return dependency.MaximumVersion is { } maximum ? $"{dependency.MinimumVersion} – {maximum}" : $">= {dependency.MinimumVersion}";
    }

    private static StoreDependency Describe(KazoPackageDependency dependency, StoreEntry? entry, IReadOnlyDictionary<string, string> running)
    {
        string? installed = entry is { Installed: { } package, Removed: false }
            ? package.Version
            : running.GetValueOrDefault(dependency.AppId);
        StoreDependencyState state = installed is null
            ? dependency.Optional ? StoreDependencyState.Optional : StoreDependencyState.WithThisApp
            : InRange(dependency, installed) ? StoreDependencyState.Installed : StoreDependencyState.OutOfRange;
        bool isFeature = entry?.IsFeature == true;
        IReadOnlyList<string> permissions = isFeature && state is StoreDependencyState.WithThisApp or StoreDependencyState.OutOfRange
            ? [.. (entry!.App?.Latest.Capabilities ?? []).Where(StoreCatalog.IsDangerous).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            : [];
        return new StoreDependency(
            dependency.AppId,
            entry?.Name ?? dependency.AppId,
            RangeText(dependency),
            dependency.Optional,
            state,
            installed,
            isFeature,
            permissions);
    }
}
