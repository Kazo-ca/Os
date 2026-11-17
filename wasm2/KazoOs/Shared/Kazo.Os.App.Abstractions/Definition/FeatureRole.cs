namespace Kazo.Os.App.Abstractions;

/// <summary>
/// A role a feature declares (ADR 0247 §1), as the « VM manager » of a virtual machine feature: an
/// administrator grants it to an ordinary user in Settings ▸ Users or with <c>sudo kazorole grant</c>;
/// every administrator holds it implicitly. It opens entry points; each sensitive operation still asks
/// the OS's authentication (<c>KazoFeatureAuthentication</c>).
/// </summary>
/// <param name="Id">The identifier, <c>&lt;prefix&gt;.&lt;name&gt;</c> under the feature's <see cref="FeatureDefinition.CapabilityPrefix"/>.</param>
/// <param name="Title">The short title shown in Settings ▸ Users.</param>
public sealed record FeatureRole(string Id, string Title)
{
    /// <summary>Gets what the role lets its holder do, shown under its title.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets what the role opens: suffixes of the feature's entry points, and permissions the feature
    /// declares that the role grants its holder for those entry points (ADR 0247 §1).
    /// </summary>
    public IReadOnlyList<string> Grants { get; init; } = [];
}
