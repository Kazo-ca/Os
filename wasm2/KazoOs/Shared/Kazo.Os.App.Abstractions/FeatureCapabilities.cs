using System.Collections.Frozen;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The rules of the permissions a feature declares (ADR 0246 §2): an identifier is
/// <c>&lt;prefix&gt;.&lt;name&gt;</c> in lowercase segments, and its prefix is not one the built-in
/// catalog uses (<c>filesystem</c>, <c>network</c>, <c>apps</c>…).
/// </summary>
public static class FeatureCapabilities
{
    /// <summary>Gets the first segment of every built-in capability: a feature cannot take them.</summary>
    public static IReadOnlySet<string> ReservedPrefixes { get; } = AppCapabilities.All
        .Select(static capability => GetPrefix(capability))
        .Append("topic-publish")
        .Append("topic-subscribe")
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Gets the first segment of an identifier, <c>x86</c> for <c>x86.vm.use</c>.</summary>
    /// <param name="capability">The identifier.</param>
    public static string GetPrefix(string capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        int dot = capability.IndexOfAny(['.', ':']);
        return dot < 0 ? capability : capability[..dot];
    }

    /// <summary>Gets whether a text is a valid prefix: one lowercase segment of letters, digits and dashes.</summary>
    /// <param name="prefix">The prefix.</param>
    public static bool IsWellFormedPrefix(string? prefix) => prefix is { Length: > 0 } && IsSegment(prefix);

    /// <summary>
    /// Gets whether an identifier can be a feature's permission: at least two lowercase segments
    /// separated by dots, whose prefix is not reserved by the built-in catalog.
    /// </summary>
    /// <param name="capability">The identifier.</param>
    public static bool IsFeaturePermission(string? capability) =>
        capability is { Length: > 0 }
        && capability.Split('.') is { Length: >= 2 } segments
        && segments.All(IsSegment)
        && !ReservedPrefixes.Contains(segments[0]);

    private static bool IsSegment(string segment) =>
        segment.Length > 0
        && char.IsAsciiLetterLower(segment[0])
        && segment.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !segment.EndsWith('-');
}
