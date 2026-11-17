using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// The packaging check of ADR 0277 §2: <c>pack</c> runs <see cref="AppPackageApiCheck"/>, the code
/// <c>kazopkg</c> runs at install time, on the package it just built, against the
/// <see cref="ApiSurfaceFile"/> of the OS the app targets. An app that calls a member the trimmed
/// publish removed fails its build instead of being refused at install time.
/// </summary>
internal static class PackageApiCheck
{
    /// <summary>
    /// Replacements for members trimmed publishes commonly lose, by the start of the reported member
    /// (<c>Namespace.Type.Method</c>). Kept short: each entry is a real case and is tested.
    /// </summary>
    private static readonly (string Member, string Replacement)[] Replacements =
    [
        ("System.Linq.Enumerable.ToHashSet", "use new HashSet<T>(source) instead"),
        ("System.String.Create", "build the string with new string(char[]) or a StringBuilder instead"),
        ("System.String.Split(System.String[],", "split with a char[] separator or Split(string, StringSplitOptions) instead"),
    ];

    /// <summary>Checks the assemblies of a built package against a surface.</summary>
    /// <param name="assemblies">The package's assemblies by file name (<see cref="AppPackage.Assemblies"/>).</param>
    /// <param name="surface">The API surface of the target OS.</param>
    /// <param name="asError">Whether a missing member fails the build (<c>KOSAPP0100</c>) or only warns (<c>KOSAPP0101</c>).</param>
    /// <returns>One diagnostic per missing member, in the order the check found them.</returns>
    public static IReadOnlyList<PackageApiDiagnostic> Check(IReadOnlyDictionary<string, byte[]> assemblies, ApiSurfaceFile surface, bool asError)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(surface);
        return [.. AppPackageApiCheck.FindMissing(assemblies, surface).Select(missing => new PackageApiDiagnostic(
            asError ? GeneratorCodes.PackageApiMissing : GeneratorCodes.PackageApiMissingWarning,
            asError,
            Describe(missing, surface.OsVersion, asError)))];
    }

    /// <summary>Gets the suggested replacement of a missing member, or <see langword="null"/>.</summary>
    /// <param name="member">The member as <see cref="MissingRuntimeApi.Member"/> reports it.</param>
    /// <returns>The suggestion, or <see langword="null"/> when none is known.</returns>
    public static string? SuggestReplacement(string member) =>
        Replacements.FirstOrDefault(entry => member.StartsWith(entry.Member, StringComparison.Ordinal)).Replacement;

    private static string Describe(MissingRuntimeApi missing, string osVersion, bool asError)
    {
        string text = $"{missing.Assembly} calls {missing.Member}, which Kazo OS {osVersion} (trimmed) does not have";
        if (SuggestReplacement(missing.Member) is { } replacement)
        {
            text += $": {replacement}";
        }

        return asError
            ? text + ". The OS would refuse to install the package (ADR 0277)."
            : text + ". KazoOsApiCheck=false: the package is written, but the OS may refuse to install it (ADR 0277).";
    }
}

/// <summary>One finding of <see cref="PackageApiCheck"/>.</summary>
/// <param name="Code"><c>KOSAPP0100</c> (error) or <c>KOSAPP0101</c> (warning).</param>
/// <param name="IsError">Whether the build fails.</param>
/// <param name="Message">The text, with the member and a replacement when one is known.</param>
internal sealed record PackageApiDiagnostic(string Code, bool IsError, string Message)
{
    /// <summary>Formats the finding as an MSBuild line attributed to <paramref name="origin"/>.</summary>
    /// <param name="origin">The manifest or project the package comes from.</param>
    /// <returns>One line, e.g. <c>/x/manifest.json : error KOSAPP0100: ...</c>.</returns>
    public string ToMsBuildLine(string origin) => $"{origin} : {(IsError ? "error" : "warning")} {Code}: {Message}";
}
