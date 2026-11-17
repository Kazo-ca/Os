using System.Collections.Frozen;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The rules of a drive provider declaration (ADR 0282 §1 and §4) that the manifest validator, the
/// manifest generator, the package installer and the Store share: the form of a type, the types the
/// OS keeps for itself, and the form of a declared host.
/// </summary>
public static class AppDriveProviders
{
    /// <summary>The longest filesystem type a provider may declare, in characters.</summary>
    public const int MaxTypeLength = 32;

    /// <summary>
    /// Gets the filesystem types no feature may declare (ADR 0282 §1): the network drives the OS
    /// ships (<c>hostfs</c>, <c>dropbox</c>, <c>onedrive</c>, <c>s3</c>), reserved even when a
    /// shipped connector is left out of the build (§5), the OS's own mounts (<c>idbfs</c> for
    /// <c>/</c>, <c>proc</c>, <c>devfs</c>), and the usual kernel types a user may meet in
    /// <c>/proc/mounts</c> on Linux, so that <c>df -T</c> never shows a third-party drive under one.
    /// </summary>
    public static IReadOnlySet<string> ReservedTypes { get; } = new[]
    {
        "hostfs", "dropbox", "onedrive", "s3",
        "idbfs", "proc", "procfs", "devfs", "sysfs", "devtmpfs", "tmpfs", "ramfs", "devpts",
        "cgroup", "cgroup2", "overlay", "squashfs", "fuse", "nfs", "nfs4", "cifs", "smb", "smb3",
        "ext2", "ext3", "ext4", "btrfs", "xfs", "vfat", "exfat", "ntfs", "iso9660", "none"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Determines whether <paramref name="type"/> has the form of a filesystem type: lowercase ASCII
    /// letters, digits and single dashes between them, starting with a letter, at most
    /// <see cref="MaxTypeLength"/> characters. Says nothing about <see cref="ReservedTypes"/>.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns><see langword="true"/> when the form is valid.</returns>
    public static bool IsValidType(string? type) =>
        type is { Length: > 0 and <= MaxTypeLength } && type[0] is >= 'a' and <= 'z' && IsDashedName(type);

    /// <summary>Determines whether a valid <paramref name="type"/> is one the OS keeps for itself (<see cref="ReservedTypes"/>).</summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns><see langword="true"/> when no feature may declare it.</returns>
    public static bool IsReservedType(string? type) => type is not null && ReservedTypes.Contains(type);

    /// <summary>
    /// Determines whether <paramref name="name"/> is a valid key of a form field: the same form as a
    /// type (<c>bucket</c>, <c>access-key-id</c>), without the length limit of a type beyond 64.
    /// </summary>
    /// <param name="name">The field name to inspect.</param>
    /// <returns><see langword="true"/> when the form is valid.</returns>
    public static bool IsValidFieldName(string? name) =>
        name is { Length: > 0 and <= 64 } && name[0] is >= 'a' and <= 'z' && IsDashedName(name);

    /// <summary>
    /// Determines whether <paramref name="host"/> is a host a provider may declare (ADR 0282 §4): a
    /// lowercase DNS name of at least two labels (<c>api.pcloud.com</c>), or a subdomain wildcard
    /// <c>*.</c> followed by such a name (<c>*.pcloud.com</c>). No scheme, port, path or IP address:
    /// the scheme is always <c>https</c>. A bare <c>*</c>, a wildcard on a single label
    /// (<c>*.com</c>) and a wildcard inside a name are refused.
    /// </summary>
    /// <param name="host">The host to inspect.</param>
    /// <returns><see langword="true"/> when the host may be declared.</returns>
    /// <remarks>
    /// The same rule as the hosts of an app's direct network access (<see cref="DirectNetworkHosts"/>,
    /// ADR 0282 §4), written in its canonical form: lower case, ASCII, no trailing dot.
    /// </remarks>
    public static bool IsValidHost(string? host) =>
        DirectNetworkHosts.TryParse(host, out string canonical, out _) && string.Equals(canonical, host, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether <paramref name="address"/> is an absolute <c>https</c> address without
    /// user information, as every OAuth address of a declaration must be (ADR 0282 §3).
    /// </summary>
    /// <param name="address">The address to inspect.</param>
    /// <returns><see langword="true"/> when the address may be declared.</returns>
    public static bool IsValidHttpsAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        && uri.UserInfo.Length == 0
        && uri.Host.Length > 0;

    /// <summary>Lowercase letters and digits in groups separated by one dash, with no dash at either end.</summary>
    private static bool IsDashedName(string value) =>
        value[^1] != '-'
        && !value.Contains("--", StringComparison.Ordinal)
        && value.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
