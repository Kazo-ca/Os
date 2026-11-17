using Kazo.Os.AppSdk.Icons;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The icon and the name of the service behind a network drive (ADR 0267 §11, ADR 0282 §5), read from
/// the description the OS gives of each type (<see cref="KazoDriveInfo.DescribeNetworkDriveTypesAsync"/>):
/// the Explorer names no service itself. Two accounts often carry the same e-mail address, so the
/// service's own mark tells a Dropbox drive from a OneDrive one.
/// </summary>
internal static class NetworkDriveIcons
{
    /// <summary>A folder of this computer (the host folder type).</summary>
    public const string HostFolderType = "hostfs";

    /// <summary>The icon of a type nobody describes: a cloud.</summary>
    public static readonly (IconLibrary Library, string Name) Generic = (IconLibrary.Lucide, "cloud");

    /// <summary>
    /// The glyph of an icon written <c>&lt;library&gt;:&lt;name&gt;</c> (<see cref="KazoNetworkDriveTypeInfo.Icon"/>);
    /// <see cref="Generic"/> when it is missing or names no library the SDK has.
    /// </summary>
    /// <param name="icon">The icon, e.g. <c>bootstrap:dropbox</c>.</param>
    public static (IconLibrary Library, string Name) Parse(string? icon)
    {
        int colon = icon?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        return colon > 0 && colon < icon!.Length - 1 && Enum.TryParse(icon[..colon], ignoreCase: true, out IconLibrary library)
            ? (library, icon[(colon + 1)..])
            : Generic;
    }

    /// <summary>The glyph of a described type, <see cref="Generic"/> for an unknown one.</summary>
    /// <param name="type">The type, as <c>mount -t</c> takes it.</param>
    /// <param name="types">The descriptions the OS gave.</param>
    public static (IconLibrary Library, string Name) For(string? type, IReadOnlyList<KazoNetworkDriveTypeInfo> types) =>
        Find(type, types) is { } info ? Parse(info.Icon) : Generic;

    /// <summary>The description of a type, <see langword="null"/> when the OS does not offer it now.</summary>
    /// <param name="type">The type.</param>
    /// <param name="types">The descriptions the OS gave.</param>
    public static KazoNetworkDriveTypeInfo? Find(string? type, IReadOnlyList<KazoNetworkDriveTypeInfo> types) =>
        types.FirstOrDefault(info => string.Equals(info.Type, type, StringComparison.Ordinal));

    /// <summary>
    /// The service's own name as its provider writes it; the type itself for one the OS does not offer
    /// now; <see langword="null"/> for a folder of the computer.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="types">The descriptions the OS gave.</param>
    public static string? ServiceName(string? type, IReadOnlyList<KazoNetworkDriveTypeInfo> types) =>
        type is null or HostFolderType ? null : Find(type, types)?.DisplayName ?? type;
}
