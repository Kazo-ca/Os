using System.Text.Json;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Reads a manifest file back with the strict schema-v1 deserializer (unknown and duplicate
/// properties rejected), normalizes omitted collections and runs <see cref="AppManifestValidator"/>.
/// </summary>
public static class ManifestFileReader
{
    /// <summary>Reads and validates <paramref name="path"/>.</summary>
    /// <param name="path">Manifest file path.</param>
    /// <param name="manifest">The manifest when valid.</param>
    /// <param name="diagnostics">Problems found; empty when valid.</param>
    /// <returns><see langword="true"/> when the file holds a valid manifest.</returns>
    public static bool TryRead(string path, out AppManifest? manifest, out IReadOnlyList<GeneratorDiagnostic> diagnostics) =>
        TryRead(path, out manifest, out _, out diagnostics);

    /// <summary>
    /// Reads and validates <paramref name="path"/> and, for a feature (ADR 0260 §2), the children in
    /// <c>entrypoints/*.json</c> next to it, each validated as a manifest of its kind and against the feature.
    /// </summary>
    /// <param name="path">Manifest file path.</param>
    /// <param name="manifest">The manifest when valid.</param>
    /// <param name="children">The feature's children when valid; empty for an app.</param>
    /// <param name="diagnostics">Problems found; empty when valid.</param>
    /// <returns><see langword="true"/> when the file, and the children, hold valid manifests.</returns>
    public static bool TryRead(string path, out AppManifest? manifest, out IReadOnlyList<AppManifest> children, out IReadOnlyList<GeneratorDiagnostic> diagnostics)
    {
        children = [];
        if (!TryReadOne(path, out manifest, out diagnostics))
        {
            return false;
        }

        if (manifest!.Kind != AppKind.Feature)
        {
            return true;
        }

        string directory = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, ManifestWriter.EntryPointsDirectoryName);
        List<AppManifest> read = [];
        List<GeneratorDiagnostic> problems = [];
        IEnumerable<string> childPaths = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal) : [];
        foreach (string childPath in childPaths)
        {
            if (TryReadOne(childPath, out AppManifest? child, out IReadOnlyList<GeneratorDiagnostic> childProblems))
            {
                read.Add(child!);
            }
            else
            {
                problems.AddRange(childProblems);
            }
        }

        problems.AddRange(AppManifestValidator.ValidateFeature(manifest, read).Errors
            .Select(error => new GeneratorDiagnostic(GeneratorCodes.InvalidManifest, path, $"{error.Path}: {error.Message} ({error.Code})")));
        if (problems.Count > 0)
        {
            manifest = null;
            diagnostics = problems;
            return false;
        }

        children = read;
        return true;
    }

    private static bool TryReadOne(string path, out AppManifest? manifest, out IReadOnlyList<GeneratorDiagnostic> diagnostics)
    {
        manifest = null;
        try
        {
            manifest = AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(path)));
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            diagnostics = [new GeneratorDiagnostic(GeneratorCodes.UnreadableManifest, path, exception.Message)];
            return false;
        }

        diagnostics = [.. AppManifestValidator.Validate(manifest).Errors
            .Select(error => new GeneratorDiagnostic(GeneratorCodes.InvalidManifest, path, $"{error.Path}: {error.Message} ({error.Code})"))];
        if (diagnostics.Count > 0)
        {
            manifest = null;
            return false;
        }

        return true;
    }
}
