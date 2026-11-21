using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Apps;

/// <summary>
/// The <c>/etc/kazoos/features.config</c> file of ADR 0287: an ADR 0011 <c>.config</c> document read
/// without a schema, where only the keys of the <c>[disabled]</c> section count. A feature listed there
/// stays installed but does not run, nor do its entry points and the apps that need it.
/// </summary>
/// <remarks>
/// <code>
/// # /etc/kazoos/features.config
/// [disabled]
/// ca.kazo.x86=true
/// </code>
/// The file is machine-wide only (answer 306 A): there is no per-user layer, unlike <c>apps.config</c>.
/// </remarks>
public static class FeaturesConfigDocument
{
    /// <summary>Section holding the disabled feature IDs.</summary>
    public const string Section = "disabled";

    /// <summary>The machine file, <c>system:system 0644</c>.</summary>
    public static VirtualPath MachinePath { get; } = VirtualPath.Parse("/etc/kazoos/features.config");

    /// <summary>Header of the file written by the first disable, so an administrator edits a real, documented file.</summary>
    public const string DefaultContent =
        "# Features disabled for every user of this machine (ADR 0287).\n"
        + "# One feature ID per line under [disabled], set to true. Remove the line (or set it to false)\n"
        + "# to enable it again. A disabled feature stays installed: its entry points and the apps that\n"
        + "# need it do not run. Only feature IDs count. No data is deleted.\n"
        + "[disabled]\n";

    /// <summary>
    /// Reads the disabled feature IDs of the file against a catalog. Never fails: a bad line is skipped
    /// and reported in <see cref="FeaturesConfigReadResult.Warnings"/>.
    /// </summary>
    /// <param name="content">File content, or <see langword="null"/> when the file is absent or unreadable (nothing disabled).</param>
    /// <param name="catalog">Catalog of this build.</param>
    /// <remarks>
    /// The last occurrence of a key wins. <c>true</c> disables, <c>false</c> does nothing; another value
    /// is a warning. An ID unknown to this build is ignored silently (the same file may serve several
    /// builds); an ID that is not a feature (an entry point, an ordinary or a system app) is ignored with
    /// a warning, so the file cannot stop what ADR 0287 does not cover.
    /// </remarks>
    public static FeaturesConfigReadResult Read(string? content, AppCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrEmpty(content))
        {
            return FeaturesConfigReadResult.Empty;
        }

        Dictionary<string, ConfigLine> lastById = new(StringComparer.Ordinal);
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind == ConfigLineKind.KeyValue
                && !line.InMalformedSection
                && string.Equals(line.Group, Section, StringComparison.Ordinal)
                && line.Key is { Length: > 0 } key)
            {
                lastById[key] = line;
            }
        }

        HashSet<string> disabled = new(StringComparer.Ordinal);
        List<string> warnings = [];
        string path = MachinePath.Value;
        foreach ((string appId, ConfigLine line) in lastById)
        {
            string value = (line.Value ?? string.Empty).Trim();
            bool isTrue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            if (!isTrue && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"apps: {path}:{line.LineNumber}: '{value}' is not true or false, line ignored");
                continue;
            }

            if (!isTrue || !catalog.Manifests.TryGetValue(appId, out AppManifest? manifest))
            {
                continue;
            }

            if (manifest.Kind != AppKind.Feature || catalog.IsSystemApp(appId))
            {
                warnings.Add($"apps: {path}:{line.LineNumber}: {appId} is not a feature and cannot be disabled here, line ignored");
                continue;
            }

            disabled.Add(appId);
        }

        return new FeaturesConfigReadResult(disabled, warnings);
    }

    /// <summary>Marks a feature disabled, keeping every other line exactly.</summary>
    /// <param name="content">Current content, or <see langword="null"/> for a new file.</param>
    /// <param name="featureId">Feature to write as <c>true</c>.</param>
    public static string MarkDisabled(string? content, string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        return ConfigDocumentEditor.SetValue(string.IsNullOrEmpty(content) ? DefaultContent : content, Section, featureId, "true");
    }

    /// <summary>Removes every line of a feature from <c>[disabled]</c>, keeping every other line exactly.</summary>
    /// <param name="content">Current content.</param>
    /// <param name="featureId">Feature to enable again.</param>
    public static string MarkEnabled(string? content, string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        return ConfigDocumentEditor.RemoveValue(content ?? string.Empty, Section, featureId);
    }
}

/// <summary>Disabled feature IDs read from <c>/etc/kazoos/features.config</c> (ADR 0287).</summary>
/// <param name="DisabledFeatureIds">Known, non-system feature IDs set to <c>true</c>.</param>
/// <param name="Warnings">One <c>/var/log/syslog</c> line per ignored line.</param>
public sealed record FeaturesConfigReadResult(IReadOnlySet<string> DisabledFeatureIds, IReadOnlyList<string> Warnings)
{
    /// <summary>Nothing disabled, no warning.</summary>
    public static FeaturesConfigReadResult Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), []);
}
