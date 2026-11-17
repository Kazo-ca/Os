using System.Collections.Concurrent;

namespace Contoso.Sdk35DriveProvider;

/// <summary>
/// The spaces of the in-memory service, by name. Public so the compatibility tests, which load this
/// assembly in a context of its own, can drive the service by reflection (network down, revoked
/// passphrase, a file too large to read) without referencing it.
/// </summary>
public static class MemorySpaces
{
    /// <summary>The passphrase a new space accepts.</summary>
    public const string DefaultPassphrase = "passphrase-1";

    private static readonly ConcurrentDictionary<string, MemorySpace> Spaces = new(StringComparer.Ordinal);

    /// <summary>Sets whether a space answers; <see langword="false"/> stands for no network.</summary>
    /// <param name="space">The space's name.</param>
    /// <param name="online">Whether it answers.</param>
    public static void SetOnline(string space, bool online) => Get(space).Online = online;

    /// <summary>Sets the passphrase a space accepts; drives that lend another one are refused.</summary>
    /// <param name="space">The space's name.</param>
    /// <param name="passphrase">The passphrase it accepts.</param>
    public static void SetPassphrase(string space, string passphrase) => Get(space).ValidPassphrase = passphrase;

    /// <summary>Places a file of <paramref name="length"/> bytes without holding its content.</summary>
    /// <param name="space">The space's name.</param>
    /// <param name="path">The file's path from the space's root (<c>/big.bin</c>).</param>
    /// <param name="length">The file's size in bytes.</param>
    public static void PlaceFile(string space, string path, long length) => Get(space).PlaceFile(path, length);

    /// <summary>Gets a space, created empty the first time its name is used.</summary>
    internal static MemorySpace Get(string space) => Spaces.GetOrAdd(space, static _ => new MemorySpace());
}
