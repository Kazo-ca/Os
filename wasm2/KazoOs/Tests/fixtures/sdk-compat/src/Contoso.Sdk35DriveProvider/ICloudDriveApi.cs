using Kazo.Os.AppSdk.Drives;

namespace Contoso.Sdk35DriveProvider;

/// <summary>One file or folder as the service describes it.</summary>
/// <param name="Name">The entry's name.</param>
/// <param name="IsFolder">Whether the entry is a folder.</param>
/// <param name="Size">The size of a file in bytes; 0 for a folder.</param>
/// <param name="Modified">When the service last changed the entry, or <see langword="null"/>.</param>
/// <param name="Revision">The service's revision of a file, or <see langword="null"/> when it keeps none.</param>
public sealed record CloudDriveItem(string Name, bool IsFolder, long Size, DateTimeOffset? Modified, string? Revision);

/// <summary>How an upload treats a file that already has the name.</summary>
public enum CloudDriveUploadMode
{
    /// <summary>Replace it, or create the file.</summary>
    Overwrite,

    /// <summary>Fail with <see cref="KazoDriveErrorKind.Exists"/> when the name is taken.</summary>
    Create,
}

/// <summary>
/// The calls the provider makes to the service. <see cref="CloudDriveHttpApi"/> sends them to the
/// real API; the tests give an in-memory service instead, so they never reach a real one. Paths are
/// the service's own, written with <c>/</c> from its root (<c>/Photos/2026/a.jpg</c>).
/// </summary>
/// <remarks>
/// Every method reports a failure as <see cref="KazoDriveException"/> with the right
/// <see cref="KazoDriveErrorKind"/>: that is what the OS turns into the error a program sees
/// (<c>ENOENT</c>, <c>EEXIST</c>…), and what the conformance suite checks.
/// </remarks>
public interface ICloudDriveApi
{
    /// <summary>Describes one entry; <see cref="KazoDriveErrorKind.NotFound"/> when nothing is there.</summary>
    Task<CloudDriveItem> GetItemAsync(string path, CancellationToken cancellationToken);

    /// <summary>Lists the direct children of a folder; <see cref="KazoDriveErrorKind.TypeMismatch"/> on a file.</summary>
    Task<IReadOnlyList<CloudDriveItem>> GetChildrenAsync(string path, CancellationToken cancellationToken);

    /// <summary>Downloads a file; <see cref="KazoDriveErrorKind.FileTooLarge"/> above <see cref="KazoDriveLimits.MaxFileBytes"/>.</summary>
    Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a whole file; with <paramref name="revision"/>, only when the file still has it
    /// (<see cref="KazoDriveErrorKind.RevisionConflict"/> otherwise).
    /// </summary>
    Task<CloudDriveItem> UploadAsync(string path, ReadOnlyMemory<byte> content, CloudDriveUploadMode mode, string? revision, CancellationToken cancellationToken);

    /// <summary>Creates a folder; <see cref="KazoDriveErrorKind.Exists"/> when the name is taken.</summary>
    Task CreateFolderAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes an entry; a folder with entries needs <paramref name="recursive"/> (<see cref="KazoDriveErrorKind.NotEmpty"/>).</summary>
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    /// <summary>Moves or renames an entry, with everything inside a folder.</summary>
    Task MoveAsync(string from, string to, CancellationToken cancellationToken);

    /// <summary>Gets the size of the account's storage and the space used, or <see langword="null"/> when the service does not say.</summary>
    Task<KazoDriveQuota?> GetQuotaAsync(CancellationToken cancellationToken);
}
