using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>
/// The version range of one declared dependency (ADR 0005, ADR 0248 §1): from
/// <see cref="AppDependencyManifest.MinimumVersion"/> to <see cref="AppDependencyManifest.MaximumVersion"/>,
/// both included, exactly as <see cref="AppCatalog"/> checks it when the catalog is built.
/// </summary>
public static class AppDependencyRange
{
    /// <summary>Gets whether <paramref name="version"/> satisfies <paramref name="dependency"/>.</summary>
    /// <param name="dependency">A dependency of a manifest.</param>
    /// <param name="version">A version of the dependency.</param>
    /// <remarks>A range that cannot be read accepts nothing: the manifest validator refuses such a manifest anyway.</remarks>
    public static bool Contains(AppDependencyManifest dependency, SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (!SemanticVersion.TryParse(dependency.MinimumVersion, out SemanticVersion minimum) || version.CompareTo(minimum) < 0)
        {
            return false;
        }

        return dependency.MaximumVersion is null
            || (SemanticVersion.TryParse(dependency.MaximumVersion, out SemanticVersion maximum) && version.CompareTo(maximum) <= 0);
    }

    /// <summary>Writes the range as <c>kazopkg</c> and the errors show it: <c>&gt;= 1.0.0</c> or <c>1.0.0 - 1.9.0</c>.</summary>
    /// <param name="dependency">A dependency of a manifest.</param>
    public static string Describe(AppDependencyManifest dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return dependency.MaximumVersion is { } maximum
            ? $"{dependency.MinimumVersion} - {maximum}"
            : $">= {dependency.MinimumVersion}";
    }
}
