using System.Diagnostics.CodeAnalysis;

namespace Kazo.Os.AppSdk.Drives;

/// <summary>
/// A path inside a drive a <see cref="KazoDriveProvider"/> opened (ADR 0282 §2): a list of names
/// relative to the drive's root, never the Kazo OS path under <c>/mnt</c>. <see cref="Root"/> is the
/// drive itself. Each name is checked: it is not empty, not <c>.</c> or <c>..</c>, and holds no
/// <c>/</c>, <c>\</c> or control character, so a provider never builds a service path that escapes
/// the drive's root.
/// </summary>
public sealed class KazoDrivePath : IEquatable<KazoDrivePath>
{
    /// <summary>The longest name of one entry, in UTF-16 characters (the usual <c>NAME_MAX</c>).</summary>
    public const int MaxNameLength = 255;

    private readonly string[] segments;

    private KazoDrivePath(string[] segments) => this.segments = segments;

    /// <summary>Gets the root of the drive: no segment.</summary>
    public static KazoDrivePath Root { get; } = new([]);

    /// <summary>Gets the names from the root to the entry; empty for <see cref="Root"/>.</summary>
    public IReadOnlyList<string> Segments => segments;

    /// <summary>Gets whether the path is the drive's root.</summary>
    public bool IsRoot => segments.Length == 0;

    /// <summary>Gets the last name, or an empty string for <see cref="Root"/>.</summary>
    public string Name => segments.Length == 0 ? string.Empty : segments[^1];

    /// <summary>Gets the folder that holds the entry, or <see langword="null"/> for <see cref="Root"/>.</summary>
    public KazoDrivePath? Parent => segments.Length == 0 ? null : new(segments[..^1]);

    /// <summary>Creates a path from names, checking each one.</summary>
    /// <param name="segments">The names from the root.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">A name is empty, <c>.</c>, <c>..</c>, too long, or holds a separator or a control character.</exception>
    public static KazoDrivePath FromSegments(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        string[] copy = [.. segments];
        foreach (string segment in copy)
        {
            if (!IsValidName(segment))
            {
                throw new ArgumentException($"'{segment}' is not a valid name in a drive path.", nameof(segments));
            }
        }

        return copy.Length == 0 ? Root : new(copy);
    }

    /// <summary>
    /// Parses a path written with <c>/</c> between names (<c>/Photos/2026</c>); a leading or
    /// trailing <c>/</c> is optional, and <c>/</c> or an empty string is <see cref="Root"/>.
    /// </summary>
    /// <param name="path">The path to parse.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">A name is not valid (see <see cref="FromSegments"/>), or two <c>/</c> follow each other.</exception>
    public static KazoDrivePath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string trimmed = path.Trim('/');
        return trimmed.Length == 0 ? Root : FromSegments(trimmed.Split('/'));
    }

    /// <summary>Parses a path like <see cref="Parse"/>, without throwing.</summary>
    /// <param name="path">The path to parse.</param>
    /// <param name="result">The path, or <see langword="null"/> when it is not valid.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is valid.</returns>
    public static bool TryParse(string? path, [NotNullWhen(true)] out KazoDrivePath? result)
    {
        result = null;
        if (path is null)
        {
            return false;
        }

        string trimmed = path.Trim('/');
        if (trimmed.Length == 0)
        {
            result = Root;
            return true;
        }

        string[] names = trimmed.Split('/');
        if (!names.All(static name => IsValidName(name)))
        {
            return false;
        }

        result = new(names);
        return true;
    }

    /// <summary>Determines whether <paramref name="name"/> may be one name of a drive path.</summary>
    /// <param name="name">The name to inspect.</param>
    /// <returns><see langword="true"/> when it is valid.</returns>
    public static bool IsValidName([NotNullWhen(true)] string? name) =>
        name is { Length: > 0 and <= MaxNameLength }
        && name is not "." and not ".."
        && !name.Any(static c => c is '/' or '\\' || char.IsControl(c));

    /// <summary>Returns the path of the entry <paramref name="name"/> in this folder.</summary>
    /// <param name="name">The entry's name.</param>
    /// <returns>The longer path.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid name.</exception>
    public KazoDrivePath Append(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid name in a drive path.", nameof(name));
        }

        return new([.. segments, name]);
    }

    /// <summary>Writes the path with <c>/</c> before each name; <c>/</c> for <see cref="Root"/>.</summary>
    /// <returns>The path, as <c>/Photos/2026</c>.</returns>
    public override string ToString() => "/" + string.Join('/', segments);

    /// <inheritdoc />
    public bool Equals(KazoDrivePath? other) =>
        other is not null && segments.AsSpan().SequenceEqual(other.segments);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KazoDrivePath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach (string segment in segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Compares two paths name by name, with an ordinal comparison.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when both hold the same names.</returns>
    public static bool operator ==(KazoDrivePath? left, KazoDrivePath? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Compares two paths name by name, with an ordinal comparison.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when the names differ.</returns>
    public static bool operator !=(KazoDrivePath? left, KazoDrivePath? right) => !(left == right);
}
