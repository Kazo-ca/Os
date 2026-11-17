namespace Kazo.Os.AppSdk.Drives;

/// <summary>The limits the OS applies to every network drive, shipped or provided (ADR 0282 §3).</summary>
public static class KazoDriveLimits
{
    /// <summary>
    /// The largest file Kazo OS reads or writes whole, in bytes: 128 MiB (ADR 0245, ADR 0267 §10).
    /// A provider throws <see cref="KazoDriveErrorKind.FileTooLarge"/> for a larger file instead
    /// of downloading it; the file stays listed.
    /// </summary>
    public const long MaxFileBytes = 128L * 1024 * 1024;
}

/// <summary>Whether an entry of a drive is a file or a folder.</summary>
public enum KazoDriveEntryKind
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A folder.</summary>
    Folder
}

/// <summary>One entry of a drive, as its service describes it (ADR 0282 §2).</summary>
/// <param name="Name">The entry's name, a valid <see cref="KazoDrivePath"/> name.</param>
/// <param name="Kind">File or folder.</param>
/// <param name="Length">The size of a file in bytes; 0 for a folder.</param>
/// <param name="LastModifiedUtc">When the service last changed the file, or <see langword="null"/> when it does not say (most folders).</param>
public sealed record KazoDriveEntry(string Name, KazoDriveEntryKind Kind, long Length, DateTimeOffset? LastModifiedUtc)
{
    /// <summary>
    /// Gets the service's own revision of a file (Dropbox's <c>rev</c>, an HTTP <c>ETag</c>), or
    /// <see langword="null"/> when the service keeps none. The OS gives it back to
    /// <see cref="KazoDriveFolder.WriteAsync"/> so that a write never replaces a file another
    /// device changed in between (ADR 0267 §4).
    /// </summary>
    public string? Revision { get; init; }
}

/// <summary>The size and free space of a drive, as its service reports them.</summary>
/// <param name="TotalBytes">The size of the storage, in bytes.</param>
/// <param name="FreeBytes">The free space, in bytes; never more than <paramref name="TotalBytes"/>.</param>
public sealed record KazoDriveQuota(long TotalBytes, long FreeBytes);

/// <summary>Whether a drive can be used now (ADR 0282 §2), as <see cref="KazoDriveFolder.GetStateAsync"/> says.</summary>
public enum KazoDriveState
{
    /// <summary>The service answers: the drive is mounted connected.</summary>
    Ready,

    /// <summary>The service refused the account's sign-in: the drive stays disconnected until the user signs in again in Settings.</summary>
    SignInRequired,

    /// <summary>
    /// The service cannot be reached now (no network, an outage): the drive is mounted disconnected
    /// and reconnects by itself after <see cref="IKazoDriveProviderContext.NotifyAvailabilityChanged"/>.
    /// </summary>
    Unavailable
}

/// <summary>
/// Why an operation of a <see cref="KazoDriveFolder"/> failed (ADR 0282 §2). The OS turns each into
/// the POSIX error an app or a shell command sees, as for the shipped connectors.
/// </summary>
public enum KazoDriveErrorKind
{
    /// <summary>Nothing at the path (<c>ENOENT</c>).</summary>
    NotFound,

    /// <summary>An entry already has the name (<c>EEXIST</c>).</summary>
    Exists,

    /// <summary>The service cannot be reached: the OS disconnects the drive (<c>EIO</c>) until it is available again.</summary>
    Unavailable,

    /// <summary>The service refused the account's sign-in: the OS disconnects the drive and asks the user to sign in again (<c>EACCES</c>).</summary>
    SignInRequired,

    /// <summary>The file changed since it was read: the conditional write was refused (<c>ESTALE</c>).</summary>
    RevisionConflict,

    /// <summary>The file is larger than <see cref="KazoDriveLimits.MaxFileBytes"/> (<c>EFBIG</c>).</summary>
    FileTooLarge,

    /// <summary>The account's storage is full (<c>ENOSPC</c>).</summary>
    QuotaExceeded,

    /// <summary>A file where a folder was expected, or the reverse (<c>ENOTDIR</c>, <c>EISDIR</c>).</summary>
    TypeMismatch,

    /// <summary>A non-recursive delete of a folder that has entries (<c>ENOTEMPTY</c>).</summary>
    NotEmpty,

    /// <summary>The service refuses the name (<c>EINVAL</c>).</summary>
    InvalidName,

    /// <summary>The service refuses the operation for this account (<c>EPERM</c>).</summary>
    AccessDenied,

    /// <summary>Any other failure of the service (<c>EIO</c>).</summary>
    Other
}

/// <summary>
/// A failed operation of a <see cref="KazoDriveFolder"/> (ADR 0282 §2). A provider throws it for
/// every failure the user may meet; the OS treats any other exception as
/// <see cref="KazoDriveErrorKind.Other"/>, writes it to the feature's log, and keeps the session going.
/// </summary>
public sealed class KazoDriveException : IOException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="kind">Why the operation failed.</param>
    /// <param name="message">What happened, for the log; never a token, a key or a file's content.</param>
    /// <param name="innerException">The service's own failure, or <see langword="null"/>.</param>
    public KazoDriveException(KazoDriveErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Gets why the operation failed.</summary>
    public KazoDriveErrorKind Kind { get; }
}

/// <summary>The account a drive belongs to, as the OS knows it after the sign-in (ADR 0282 §3).</summary>
/// <param name="Id">The service's id of the account.</param>
/// <param name="DisplayName">The name the user reads (an e-mail address, a full name).</param>
public sealed record KazoDriveAccount(string Id, string DisplayName);

/// <summary>
/// What the OS gives a provider to open one drive (ADR 0282 §2): what it keeps about the drive in
/// the user's drive list, and the context that reaches the account's credentials.
/// </summary>
/// <param name="Name">The name of the drive, as the drive list shows it.</param>
/// <param name="Context">The door to the account's access token and the drive's secrets.</param>
public sealed record KazoDriveOpenRequest(string Name, IKazoDriveProviderContext Context)
{
    /// <summary>Gets the folder of the service the drive starts at, written with <c>/</c> (<c>/Work</c>), or <see langword="null"/> for the whole storage.</summary>
    public string? Root { get; init; }

    /// <summary>
    /// Gets the values of the non-secret form fields the user typed (ADR 0282 §3), by field name;
    /// empty for an OAuth connection. Secret values come through <see cref="IKazoDriveProviderContext.GetSecretAsync"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// What the OS lends a provider for one drive (ADR 0282 §2 and §3). It only reaches the account of
/// the drive being opened: never the refresh token, never another drive's secrets.
/// </summary>
public interface IKazoDriveProviderContext
{
    /// <summary>Gets the filesystem type of the drive (<c>exemple</c>).</summary>
    string Type { get; }

    /// <summary>Gets the account the drive belongs to, or <see langword="null"/> for a form connection (which has no sign-in).</summary>
    KazoDriveAccount? Account { get; }

    /// <summary>
    /// Gets a valid access token of the drive's account (OAuth connection only). The OS renews it
    /// with the refresh token it keeps sealed (ADR 0269) when it is about to expire. Call it before
    /// each request rather than keeping the token.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The access token.</returns>
    /// <exception cref="KazoDriveException">
    /// <see cref="KazoDriveErrorKind.SignInRequired"/> when the service refused to renew it, or
    /// the drive has a form connection; <see cref="KazoDriveErrorKind.Unavailable"/> when the
    /// service cannot be reached.
    /// </exception>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the value of a secret form field of this drive (<c>secret-access-key</c>), unsealed for this call.</summary>
    /// <param name="name">The field's name, as declared.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The value, or <see langword="null"/> when the drive has no such secret.</returns>
    ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the OS the service may be reachable again (the network came back): it asks the drives
    /// of this type that are disconnected as <see cref="KazoDriveState.Unavailable"/> for their state
    /// again, and reconnects those that answer <see cref="KazoDriveState.Ready"/>.
    /// </summary>
    void NotifyAvailabilityChanged();
}
