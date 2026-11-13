namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>
/// What the drive list (<c>~/.local/state/kazoos/network-drives.json</c>) keeps about one drive
/// for its source (ADR 0267 §3), so the source can open it again at the next login.
/// </summary>
/// <param name="Name">Name of the drive as its source shows it: the folder of the computer, or the account of a cloud service.</param>
/// <param name="Key">Key of the drive in the source's own store, e.g. the browser handle of a folder of the computer.</param>
/// <param name="Account">Reference of the cloud account in the account list; never a token.</param>
/// <param name="Root">Folder of the service the drive starts at; <see langword="null"/> for the whole storage.</param>
public sealed record NetworkDriveReference(string Name, string? Key = null, string? Account = null, string? Root = null);

/// <summary>What the user chose when connecting a drive (ADR 0267 §3 and §8).</summary>
/// <param name="Account">Reference of the cloud account to use; ignored by the folder of the computer.</param>
/// <param name="Root">Folder of the service to start at; <see langword="null"/> for the whole storage.</param>
public sealed record NetworkDriveRequest(string? Account = null, string? Root = null);

/// <summary>A drive its source just created, not mounted yet.</summary>
/// <param name="Folder">The drive's content.</param>
/// <param name="Reference">What the drive list keeps about it.</param>
/// <param name="DefaultMountName">Name of the default mount point under <c>/mnt</c>, e.g. <c>Dropbox</c>.</param>
public sealed record NetworkDriveCreation(IHostFolder Folder, NetworkDriveReference Reference, string DefaultMountName);

/// <summary>
/// One kind of network drive (ADR 0267 §3): <c>hostfs</c> (a folder of the computer, ADR 0181),
/// <c>dropbox</c>, <c>onedrive</c>, <c>s3</c>. The network drive service mounts what a source creates
/// or opens, under <c>/mnt</c>, with the rules of ADR 0181.
/// </summary>
/// <remarks>
/// A source never blocks a login: <see cref="OpenAsync"/> builds the folder without reaching the
/// service. Whether the drive is connected comes from <see cref="IHostFolder.QueryPermissionAsync"/>:
/// <see cref="HostFolderPermission.Unavailable"/> mounts it disconnected, and it reconnects by itself
/// after <see cref="AvailabilityChanged"/>.
/// </remarks>
public interface INetworkDriveSource
{
    /// <summary>Gets the filesystem type, as <c>mount -t</c>, <c>/proc/mounts</c> and <c>df -T</c> show it.</summary>
    string Type { get; }

    /// <summary>Gets whether a new drive can be created here (the browser has the folder dialog, the provider is configured).</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a drive, asking the user what the source needs (a folder dialog, a sign-in window);
    /// <see langword="null"/> when the user closed it.
    /// </summary>
    /// <exception cref="NotSupportedException">The source is not available here.</exception>
    /// <exception cref="InvalidOperationException">The browser asks for a recent user gesture.</exception>
    /// <exception cref="ArgumentException">The request is not usable (an unknown account, say).</exception>
    ValueTask<NetworkDriveCreation?> CreateAsync(NetworkDriveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Opens a drive of the list again, at login; <see langword="null"/> when it is gone for good.</summary>
    ValueTask<IHostFolder?> OpenAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default);

    /// <summary>Forgets a drive the user unmounted; nothing happens when it is unknown.</summary>
    ValueTask ForgetAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default);

    /// <summary>Raised when the source may be reachable again (the network came back, an account signed in again).</summary>
    event Action? AvailabilityChanged;
}
