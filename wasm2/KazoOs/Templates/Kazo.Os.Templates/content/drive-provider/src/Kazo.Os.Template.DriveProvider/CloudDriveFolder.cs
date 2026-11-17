using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.Template.DriveProvider;

/// <summary>
/// One drive: each operation of <see cref="KazoDriveFolder"/> becomes one call to
/// <see cref="ICloudDriveApi"/>, on the service path of the drive path.
/// </summary>
/// <remarks>
/// A file's revision is the service's: a conditional write sends it, so a file another device
/// changed is never replaced (ADR 0267 §4).
/// </remarks>
internal sealed class CloudDriveFolder : KazoDriveFolder
{
    private readonly ICloudDriveApi _api;
    private readonly string[] _root;

    /// <summary>Creates the drive; nothing is sent here.</summary>
    /// <param name="api">The service.</param>
    /// <param name="root">The folder of the account the drive starts at, or <see langword="null"/> for the whole account.</param>
    public CloudDriveFolder(ICloudDriveApi api, KazoDrivePath? root)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _root = [.. root?.Segments ?? []];
    }

    /// <inheritdoc />
    public override async ValueTask<KazoDriveState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _api.GetItemAsync(PathOf(KazoDrivePath.Root), cancellationToken).ConfigureAwait(false);
            return KazoDriveState.Ready;
        }
        catch (KazoDriveException exception) when (exception.Kind == KazoDriveErrorKind.SignInRequired)
        {
            return KazoDriveState.SignInRequired;
        }
        catch (KazoDriveException)
        {
            // Down, or the root folder is gone: the drive waits disconnected.
            return KazoDriveState.Unavailable;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        CloudDriveItem item = await _api.GetItemAsync(PathOf(path), cancellationToken).ConfigureAwait(false);
        return ToEntry(path.Name, item);
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<CloudDriveItem> children = await _api.GetChildrenAsync(PathOf(path), cancellationToken).ConfigureAwait(false);

        // A name the OS could not hold (a separator, a control character) is left out.
        return [.. children.Where(static item => KazoDrivePath.IsValidName(item.Name)).Select(static item => ToEntry(item.Name, item))];
    }

    /// <inheritdoc />
    public override async ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await _api.DownloadAsync(PathOf(path), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        await _api.UploadAsync(PathOf(path), content, CloudDriveUploadMode.Overwrite, ifRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask CreateFileAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        await _api.UploadAsync(PathOf(path), ReadOnlyMemory<byte>.Empty, CloudDriveUploadMode.Create, revision: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        await _api.CreateFolderAsync(PathOf(path), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        await _api.DeleteAsync(PathOf(path), recursive, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The service moves in one call, folders with their content: the OS never copies.</remarks>
    public override async ValueTask<bool> TryMoveAsync(KazoDrivePath from, KazoDrivePath to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        await _api.MoveAsync(PathOf(from), PathOf(to), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public override async ValueTask<KazoDriveQuota?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
        await _api.GetQuotaAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        (_api as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The service path of <paramref name="path"/>: <c>/</c>, the root folder, then its names.</summary>
    private string PathOf(KazoDrivePath path) => "/" + string.Join('/', _root.Concat(path.Segments));

    private static KazoDriveEntry ToEntry(string name, CloudDriveItem item) =>
        new(name, item.IsFolder ? KazoDriveEntryKind.Folder : KazoDriveEntryKind.File, item.IsFolder ? 0 : item.Size, item.Modified)
        {
            Revision = item.IsFolder ? null : item.Revision,
        };
}
