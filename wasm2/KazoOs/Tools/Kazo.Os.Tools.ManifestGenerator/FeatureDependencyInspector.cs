using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// ADR 0262 §3 (<c>KOSAPP0034</c>): an app that asks for a feature's service through
/// <c>KazoFeatures</c> references the feature in its manifest's <c>Dependencies</c>. Which feature
/// provides a contract is not known at build time, so the rule only asks that the app declare at
/// least one dependency; at run time a call without a dependency on the provider gives
/// <c>NotDeclared</c> and <see langword="null"/>.
/// </summary>
public static class FeatureDependencyInspector
{
    /// <summary>Namespace of the SDK facade, compared by name (the app lives in its own load context).</summary>
    public const string FacadeNamespace = "Kazo.Os.AppSdk.Features";

    /// <summary>Name of the SDK facade.</summary>
    public const string FacadeName = "KazoFeatures";

    /// <summary>Checks an app assembly against the manifests it produced.</summary>
    /// <param name="assemblyBytes">The compiled app assembly.</param>
    /// <param name="manifests">The manifests of its apps (a feature and its children included).</param>
    /// <param name="subject">What the diagnostic names (the assembly path).</param>
    /// <returns>One diagnostic, or none.</returns>
    public static IReadOnlyList<GeneratorDiagnostic> Inspect(byte[] assemblyBytes, IReadOnlyList<AppManifest> manifests, string subject)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        ArgumentNullException.ThrowIfNull(manifests);
        if (!UsesFacade(assemblyBytes) || manifests.Any(static manifest => manifest.Dependencies.Count > 0))
        {
            return [];
        }

        return
        [
            new GeneratorDiagnostic(GeneratorCodes.FeatureDependency, subject,
                $"The app calls {FacadeName} but declares no dependency: add the feature whose service it asks for to Dependencies (Optional = true when the app runs without it, ADR 0262 §3)."),
        ];
    }

    /// <summary>Gets whether an assembly refers to the <c>KazoFeatures</c> type of the SDK.</summary>
    /// <param name="assemblyBytes">The compiled assembly.</param>
    public static bool UsesFacade(byte[] assemblyBytes)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        using PEReader pe = new(ImmutableArray.Create(assemblyBytes));
        if (!pe.HasMetadata)
        {
            return false;
        }

        MetadataReader reader = pe.GetMetadataReader();
        return reader.TypeReferences
            .Select(handle => reader.GetTypeReference(handle))
            .Any(type => reader.StringComparer.Equals(type.Name, FacadeName) && reader.StringComparer.Equals(type.Namespace, FacadeNamespace));
    }
}
