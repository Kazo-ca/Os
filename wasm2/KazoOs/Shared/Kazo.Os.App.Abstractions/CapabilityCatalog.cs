namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The catalog of capabilities the OS knows (ADR 0246 §1): the built-in catalog
/// (<see cref="AppCapabilities"/>), the bus topic permissions (ADR 0040) and the permissions the
/// installed features declare. Everything that decides a grant reads it: an identifier it does not
/// know is never granted.
/// </summary>
public interface ICapabilityCatalog
{
    /// <summary>Gets every capability the catalog describes, in identifier order (topic permissions excepted: they are open-ended).</summary>
    IReadOnlyList<CapabilityDescription> All { get; }

    /// <summary>Gets the description of a capability, or <see langword="null"/> when the catalog does not know it.</summary>
    /// <param name="capability">The identifier.</param>
    CapabilityDescription? Find(string capability);
}

/// <summary>Reads an <see cref="ICapabilityCatalog"/>.</summary>
public static class CapabilityCatalogExtensions
{
    /// <summary>Gets whether the catalog knows <paramref name="capability"/>.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="capability">The identifier.</param>
    public static bool IsKnown(this ICapabilityCatalog catalog, string capability)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(capability) is not null;
    }

    /// <summary>Gets whether <paramref name="capability"/> is known and dangerous (ADR 0049 §1).</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="capability">The identifier.</param>
    public static bool IsDangerous(this ICapabilityCatalog catalog, string capability)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(capability)?.Risk == CapabilityRisk.Dangerous;
    }

    /// <summary>Gets whether <paramref name="capability"/> is known and normal: granted at sign-in to an app that declares it.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="capability">The identifier.</param>
    public static bool IsNormal(this ICapabilityCatalog catalog, string capability)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(capability)?.Risk == CapabilityRisk.Normal;
    }

    /// <summary>
    /// Gets the permissions <paramref name="capability"/> covers, directly or through another one
    /// (ADR 0246 §3); empty for none.
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="capability">The identifier.</param>
    public static IReadOnlySet<string> GetCovered(this ICapabilityCatalog catalog, string capability)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string> covered = new(StringComparer.Ordinal);
        Queue<string> next = new([capability]);
        while (next.TryDequeue(out string? current))
        {
            foreach (string child in catalog.Find(current)?.Covers ?? [])
            {
                if (!string.Equals(child, capability, StringComparison.Ordinal) && covered.Add(child))
                {
                    next.Enqueue(child);
                }
            }
        }

        return covered;
    }

    /// <summary>
    /// Gets whether a set of granted capabilities holds <paramref name="capability"/>: granted
    /// exactly, or covered by a granted permission (ADR 0246 §3).
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="granted">The granted identifiers.</param>
    /// <param name="capability">The identifier asked for.</param>
    public static bool IsGranted(this ICapabilityCatalog catalog, IEnumerable<string> granted, string capability)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(granted);
        return granted.Any(item => string.Equals(item, capability, StringComparison.Ordinal) || catalog.GetCovered(item).Contains(capability));
    }
}

/// <summary>
/// The catalog an OS without installed features knows: the built-in capabilities, and the bus topic
/// permissions (ADR 0040), which are dangerous (ADR 0246 §3, answer 2 A): an app receives one only
/// after an approval, never silently (issue #432).
/// </summary>
public sealed class BuiltInCapabilityCatalog : ICapabilityCatalog
{
    private BuiltInCapabilityCatalog()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static BuiltInCapabilityCatalog Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<CapabilityDescription> All { get; } = [.. AppCapabilities.All
        .Order(StringComparer.Ordinal)
        .Select(AppCapabilities.Describe)];

    /// <inheritdoc />
    public CapabilityDescription? Find(string capability)
    {
        if (capability is null)
        {
            return null;
        }

        if (AppCapabilities.IsKnown(capability))
        {
            return AppCapabilities.Describe(capability);
        }

        return TopicPermissions.IsWellFormed(capability)
            ? new CapabilityDescription(
                capability,
                $"Use the bus topic of {capability}",
                CapabilityRisk.Dangerous,
                "Lets the app read or send messages on another app's bus topic, which that app exports for trusted peers only (ADR 0040).")
            : null;
    }
}

/// <summary>
/// The built-in catalog plus the permissions of a set of features (ADR 0246 §1): the features the
/// OS has installed and enabled. A feature's permission joins the catalog with its title, risk
/// statement, covered permissions and the feature's id as its provider.
/// </summary>
public sealed class FeatureCapabilityCatalog : ICapabilityCatalog
{
    private readonly ICapabilityCatalog _builtIn;
    private readonly IReadOnlyDictionary<string, CapabilityDescription> _declared;

    /// <summary>Creates the catalog.</summary>
    /// <param name="builtIn">The built-in catalog.</param>
    /// <param name="features">The manifests of the installed, enabled features; the first one (by id) that declares an identifier wins.</param>
    public FeatureCapabilityCatalog(ICapabilityCatalog builtIn, IEnumerable<AppManifest> features)
    {
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        ArgumentNullException.ThrowIfNull(features);
        Dictionary<string, CapabilityDescription> declared = new(StringComparer.Ordinal);
        foreach (AppManifest feature in features.Where(static manifest => manifest.Kind == AppKind.Feature).OrderBy(static manifest => manifest.Id, StringComparer.Ordinal))
        {
            foreach (AppProvidedCapabilityManifest capability in feature.ProvidedCapabilities ?? [])
            {
                if (builtIn.Find(capability.Id) is null)
                {
                    declared.TryAdd(capability.Id, new CapabilityDescription(capability.Id, capability.Title, capability.Risk, capability.RiskStatement)
                    {
                        ProviderId = feature.Id,
                        Covers = capability.Covers ?? [],
                    });
                }
            }
        }

        _declared = declared;
        All = [.. builtIn.All.Concat(declared.Values).OrderBy(static description => description.Capability, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyList<CapabilityDescription> All { get; }

    /// <inheritdoc />
    public CapabilityDescription? Find(string capability) =>
        capability is null ? null : _builtIn.Find(capability) ?? _declared.GetValueOrDefault(capability);
}
