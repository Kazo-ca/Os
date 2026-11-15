using Kazo.Os.AppSdk.Icons;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The icon and the name of the service behind a network drive (ADR 0267 §11). Two accounts often
/// carry the same e-mail address, so the service's own mark tells a Dropbox drive from a OneDrive
/// one. The marks come from the icon fonts the SDK already ships (Bootstrap Icons, MIT); OneDrive
/// has no glyph of its own there, so it takes Microsoft's. Benoit approved using the services' marks
/// on 2026-11-15: the user connects their own account of that service.
/// </summary>
internal static class NetworkDriveIcons
{
    /// <summary>A folder of this computer (the host folder type).</summary>
    public const string HostFolderType = "hostfs";

    /// <summary>The library and name of the icon for a drive or provider type.</summary>
    /// <param name="type">The type, as <c>mount -t</c> takes it: <c>dropbox</c>, <c>onedrive</c>, <c>s3</c>, <c>hostfs</c>.</param>
    public static (IconLibrary Library, string Name) For(string? type) => type switch
    {
        "dropbox" => (IconLibrary.Bootstrap, "dropbox"),
        "onedrive" => (IconLibrary.Bootstrap, "microsoft"),
        "s3" => (IconLibrary.Lucide, "database"),
        HostFolderType => (IconLibrary.Lucide, "hard-drive"),
        _ => (IconLibrary.Lucide, "cloud")
    };

    /// <summary>The service's own name (a brand, never translated); <see langword="null"/> for a folder of the computer.</summary>
    public static string? ServiceName(string? type) => type switch
    {
        "dropbox" => "Dropbox",
        "onedrive" => "OneDrive",
        "s3" => "S3",
        _ => null
    };
}
