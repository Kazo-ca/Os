using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem.HostFolders;

/// <summary>
/// The <c>hostfs</c> drive source (ADR 0181, ADR 0267 §3): a folder of the computer, chosen in the
/// browser's folder dialog, whose handle the browser keeps in IndexedDB under
/// <see cref="NetworkDriveReference.Key"/>.
/// </summary>
/// <remarks>
/// It never raises <c>AvailabilityChanged</c>: the browser asks for its permission again only
/// after a user gesture (<c>mount -o remount</c>, "Reconnect").
/// </remarks>
public sealed class HostFolderDriveSource : INetworkDriveSource
{
    private readonly IHostFolderAccess _access;

    /// <summary>Initializes the source.</summary>
    /// <param name="access">The browser's folder dialog and handle store.</param>
    public HostFolderDriveSource(IHostFolderAccess access)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
    }

    /// <inheritdoc />
    public string Type => HostFolderFileSystemProvider.FileSystemType;

    /// <inheritdoc />
    event Action? INetworkDriveSource.AvailabilityChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        _access.IsSupportedAsync(cancellationToken);

    /// <summary>Opens the browser's folder dialog; the request is not used.</summary>
    public async ValueTask<NetworkDriveCreation?> CreateAsync(NetworkDriveRequest request, CancellationToken cancellationToken = default)
    {
        HostFolderPick? pick = await _access.PickAsync(cancellationToken).ConfigureAwait(false);
        return pick is null
            ? null
            : new NetworkDriveCreation(pick.Folder, new NetworkDriveReference(pick.Folder.Name, pick.Key), pick.Folder.Name);
    }

    /// <inheritdoc />
    public ValueTask<IHostFolder?> OpenAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Key is { Length: > 0 } key
            ? _access.LoadAsync(key, cancellationToken)
            : ValueTask.FromResult<IHostFolder?>(null);
    }

    /// <inheritdoc />
    public ValueTask ForgetAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Key is { Length: > 0 } key ? _access.ForgetAsync(key, cancellationToken) : ValueTask.CompletedTask;
    }
}
