using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.S3;

/// <summary>
/// A bucket (or a prefix of it) seen as a folder (ADR 0267 §6): S3 has only keys, so a folder is a
/// common prefix. An empty folder is kept by a zero-byte marker object whose key ends with
/// <c>/</c>, the convention of the S3 consoles. Revisions are the service's <c>ETag</c>, and a
/// conditional write sends <c>If-Match</c>.
/// </summary>
/// <remarks>
/// <para>
/// No quota: S3 has none to report, so <c>df</c> shows <c>-</c>.
/// </para>
/// <para>
/// Opening the folder never reaches the service: at login, <see cref="QueryPermissionAsync"/> only
/// opens the keys. After a request failed for lack of network, it asks the service again before it
/// answers <see cref="HostFolderPermission.Granted"/>, so a drive does not reconnect while still offline.
/// </para>
/// </remarks>
internal sealed class S3Folder : IHostFolder, IConditionalHostFolder
{
    private const int ListPageSize = 1000;

    private readonly S3Client _client;
    private readonly Func<CancellationToken, ValueTask<S3Connection?>> _connection;
    private readonly string _bucket;
    private readonly string _prefix;
    private volatile bool _unreachable;
    private volatile bool _keysRefused;

    /// <summary>Initializes the folder.</summary>
    /// <param name="client">The S3 calls.</param>
    /// <param name="connection">Opens the account's keys; <see langword="null"/> when they cannot be opened (keyring locked, keys gone).</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="prefix">The prefix the drive starts at, without a leading or trailing <c>/</c>; empty for the whole bucket.</param>
    /// <param name="name">Name of the drive, the source shown in <c>/proc/mounts</c>.</param>
    public S3Folder(S3Client client, Func<CancellationToken, ValueTask<S3Connection?>> connection, string bucket, string prefix, string name)
    {
        _client = client;
        _connection = connection;
        _bucket = bucket;
        _prefix = prefix.Length == 0 ? string.Empty : prefix + "/";
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Gets whether the last request could not reach the service.</summary>
    internal bool IsUnreachable => _unreachable;

    /// <inheritdoc />
    public async ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default)
    {
        if (await _connection(cancellationToken).ConfigureAwait(false) is not { } connection)
        {
            // The keys cannot be opened: the user types them again (Connect a network drive).
            return HostFolderPermission.Prompt;
        }

        return _unreachable || _keysRefused ? await ProbeAsync(connection, cancellationToken).ConfigureAwait(false) : HostFolderPermission.Granted;
    }

    /// <summary>Asks the service whether the drive works now; there is no window to open for S3.</summary>
    public async ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        await _connection(cancellationToken).ConfigureAwait(false) is { } connection
            ? await ProbeAsync(connection, cancellationToken).ConfigureAwait(false)
            : HostFolderPermission.Prompt;

    /// <inheritdoc />
    public async ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        if (path.Count == 0)
        {
            return new HostFolderEntry(Name, HostFolderEntryKind.Directory, 0, null);
        }

        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(async () =>
        {
            if (await _client.HeadAsync(connection, _bucket, Key(path), cancellationToken).ConfigureAwait(false) is { } found)
            {
                return File(path[^1], found);
            }

            return await DirectoryExistsAsync(connection, path, cancellationToken).ConfigureAwait(false)
                ? new HostFolderEntry(path[^1], HostFolderEntryKind.Directory, 0, null)
                : throw new HostFolderException(HostFolderErrorKind.NotFound, $"{string.Join('/', path)} not found");
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(async () =>
        {
            string folder = FolderKey(path);
            Dictionary<string, HostFolderEntry> entries = new(StringComparer.Ordinal);
            bool exists = path.Count == 0;
            string? token = null;
            do
            {
                S3ListPage page = await _client
                    .ListAsync(connection, _bucket, folder, "/", ListPageSize, token, cancellationToken)
                    .ConfigureAwait(false);
                foreach (string prefix in page.Prefixes)
                {
                    exists = true;
                    string name = prefix[folder.Length..].TrimEnd('/');
                    if (name.Length > 0 && !name.Contains('/', StringComparison.Ordinal))
                    {
                        // A key "a" next to keys "a/…": the folder wins, Kazo OS cannot show both.
                        entries[name] = new HostFolderEntry(name, HostFolderEntryKind.Directory, 0, null);
                    }
                }

                foreach (S3Object item in page.Objects)
                {
                    exists = true;
                    string name = item.Key[folder.Length..];
                    if (name.Length > 0 && !name.Contains('/', StringComparison.Ordinal))
                    {
                        entries.TryAdd(name, File(name, item));
                    }
                }

                token = page.NextContinuationToken;
            }
            while (token is not null);

            if (!exists)
            {
                throw await _client.HeadAsync(connection, _bucket, Key(path), cancellationToken).ConfigureAwait(false) is not null
                    ? new HostFolderException(HostFolderErrorKind.TypeMismatch, $"{string.Join('/', path)} is a file")
                    : new HostFolderException(HostFolderErrorKind.NotFound, $"{string.Join('/', path)} not found");
            }

            return (IReadOnlyList<HostFolderEntry>)[.. entries.Values];
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        RequireEntry(path);
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(async () =>
        {
            try
            {
                (byte[] content, _) = await _client.GetAsync(connection, _bucket, Key(path), cancellationToken).ConfigureAwait(false);
                return content;
            }
            catch (HostFolderException exception) when (exception.Kind == HostFolderErrorKind.NotFound)
            {
                if (await DirectoryExistsAsync(connection, path, cancellationToken).ConfigureAwait(false))
                {
                    throw new HostFolderException(HostFolderErrorKind.TypeMismatch, $"{string.Join('/', path)} is a folder", exception);
                }

                throw;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default)
    {
        RequireEntry(path);
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(async () =>
        {
            await RequireParentAsync(connection, path, cancellationToken).ConfigureAwait(false);
            if (await _client.HeadAsync(connection, _bucket, Key(path), cancellationToken).ConfigureAwait(false) is not null
                || await DirectoryExistsAsync(connection, path, cancellationToken).ConfigureAwait(false))
            {
                throw new HostFolderException(HostFolderErrorKind.AlreadyExists, $"{string.Join('/', path)} exists");
            }

            if (kind == HostFolderEntryKind.Directory)
            {
                await _client.PutAsync(connection, _bucket, FolderKey(path), [], ifMatch: null, ifNoneMatch: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.PutAsync(connection, _bucket, Key(path), [], ifMatch: null, ifNoneMatch: true, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default)
    {
        RequireEntry(path);
        ArgumentNullException.ThrowIfNull(content);
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(async () =>
        {
            if (await _client.HeadAsync(connection, _bucket, Key(path), cancellationToken).ConfigureAwait(false) is null)
            {
                throw await DirectoryExistsAsync(connection, path, cancellationToken).ConfigureAwait(false)
                    ? new HostFolderException(HostFolderErrorKind.TypeMismatch, $"{string.Join('/', path)} is a folder")
                    : new HostFolderException(HostFolderErrorKind.NotFound, $"{string.Join('/', path)} not found");
            }

            await _client.PutAsync(connection, _bucket, Key(path), content, ifMatch: null, ifNoneMatch: false, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteIfMatchAsync(
        IReadOnlyList<string> path,
        byte[] content,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        RequireEntry(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(async () =>
        {
            await _client.PutAsync(connection, _bucket, Key(path), content, expectedRevision, ifNoneMatch: false, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default)
    {
        RequireEntry(path);
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(async () =>
        {
            string key = Key(path);
            if (await _client.HeadAsync(connection, _bucket, key, cancellationToken).ConfigureAwait(false) is not null)
            {
                await _client.DeleteAsync(connection, _bucket, key, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string folder = FolderKey(path);
                List<string> keys = await AllKeysAsync(connection, folder, cancellationToken).ConfigureAwait(false);
                if (keys.Count == 0)
                {
                    throw new HostFolderException(HostFolderErrorKind.NotFound, $"{string.Join('/', path)} not found");
                }

                if (!recursive && keys.Any(item => item != folder))
                {
                    throw new HostFolderException(HostFolderErrorKind.NotEmpty, $"{string.Join('/', path)} is not empty");
                }

                // The deepest keys first and the folder's own marker last, so an interrupted delete
                // leaves the folder visible with what is left in it.
                foreach (string item in keys.OrderByDescending(static item => item.Length).ThenByDescending(static item => item, StringComparer.Ordinal))
                {
                    await _client.DeleteAsync(connection, _bucket, item, cancellationToken).ConfigureAwait(false);
                }
            }

            await KeepParentAsync(connection, path, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a file with a copy on the service then a delete. A folder is not moved here: S3 has
    /// no folders to rename, so the caller copies its files one by one.
    /// </summary>
    public async ValueTask<bool> TryMoveAsync(
        IReadOnlyList<string> source,
        IReadOnlyList<string> destination,
        CancellationToken cancellationToken = default)
    {
        RequireEntry(source);
        RequireEntry(destination);
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(async () =>
        {
            if (await _client.HeadAsync(connection, _bucket, Key(source), cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }

            await _client.CopyAsync(connection, _bucket, Key(source), Key(destination), cancellationToken).ConfigureAwait(false);
            await _client.DeleteAsync(connection, _bucket, Key(source), cancellationToken).ConfigureAwait(false);
            await KeepParentAsync(connection, source, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Lists the first key of the drive: the check made before mounting, and after the network came
    /// back. Fails like any request.
    /// </summary>
    internal async ValueTask CheckAsync(CancellationToken cancellationToken)
    {
        S3Connection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(async () =>
        {
            await _client.ListAsync(connection, _bucket, _prefix, "/", 1, null, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private async ValueTask<HostFolderPermission> ProbeAsync(S3Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ListAsync(connection, _bucket, _prefix, "/", 1, null, cancellationToken).ConfigureAwait(false);
            _unreachable = false;
            _keysRefused = false;
            return HostFolderPermission.Granted;
        }
        catch (HostFolderException exception) when (exception.Kind == HostFolderErrorKind.Unavailable)
        {
            _unreachable = true;
            return HostFolderPermission.Unavailable;
        }
        catch (HostFolderException exception) when (exception.InnerException is S3ServiceException { KeysRefused: true })
        {
            _keysRefused = true;
            return HostFolderPermission.Prompt;
        }
        catch (HostFolderException exception) when (exception.Kind == HostFolderErrorKind.NotAllowed)
        {
            return HostFolderPermission.Denied;
        }
    }

    /// <summary>
    /// Runs a request and remembers how it failed: no network marks the folder unreachable, and keys
    /// the service refuses make the drive wait for new keys (<see cref="HostFolderErrorKind.Unavailable"/>
    /// disconnects it in both cases).
    /// </summary>
    private async ValueTask<T> RunAsync<T>(Func<Task<T>> request)
    {
        try
        {
            T result = await request().ConfigureAwait(false);
            _unreachable = false;
            return result;
        }
        catch (HostFolderException exception) when (exception.Kind == HostFolderErrorKind.Unavailable)
        {
            _unreachable = true;
            throw;
        }
        catch (HostFolderException exception) when (exception.InnerException is S3ServiceException { KeysRefused: true })
        {
            throw KeysRefused(exception);
        }
        catch (HostFolderException exception) when (exception.InnerException is S3ServiceException { Status: 403, Code: null })
        {
            // A refused HEAD has no body to say why: one listing tells keys refused from one object refused.
            if (await _connection(CancellationToken.None).ConfigureAwait(false) is { } connection
                && await ProbeAsync(connection, CancellationToken.None).ConfigureAwait(false) == HostFolderPermission.Prompt)
            {
                throw KeysRefused(exception);
            }

            throw;
        }
    }

    private HostFolderException KeysRefused(HostFolderException exception)
    {
        _keysRefused = true;
        return new HostFolderException(HostFolderErrorKind.Unavailable, "s3: the service refused the keys; type them again", exception);
    }

    private async ValueTask<S3Connection> ConnectAsync(CancellationToken cancellationToken) =>
        await _connection(cancellationToken).ConfigureAwait(false)
            ?? throw new HostFolderException(HostFolderErrorKind.Unavailable, "s3: the keys of this drive cannot be opened; type them again");

    private async ValueTask<bool> DirectoryExistsAsync(S3Connection connection, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        S3ListPage page = await _client.ListAsync(connection, _bucket, FolderKey(path), delimiter: null, 1, null, cancellationToken).ConfigureAwait(false);
        return page.Objects.Count > 0 || page.Prefixes.Count > 0;
    }

    private async ValueTask RequireParentAsync(S3Connection connection, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        if (path.Count <= 1)
        {
            return;
        }

        string[] parent = [.. path.Take(path.Count - 1)];
        if (!await DirectoryExistsAsync(connection, parent, cancellationToken).ConfigureAwait(false))
        {
            throw await _client.HeadAsync(connection, _bucket, Key(parent), cancellationToken).ConfigureAwait(false) is not null
                ? new HostFolderException(HostFolderErrorKind.TypeMismatch, $"{string.Join('/', parent)} is a file")
                : new HostFolderException(HostFolderErrorKind.NotFound, $"{string.Join('/', parent)} not found");
        }
    }

    /// <summary>
    /// Keeps the parent folder of a deleted or moved entry: when nothing is left under its prefix,
    /// S3 would forget the folder, so a marker is written.
    /// </summary>
    private async ValueTask KeepParentAsync(S3Connection connection, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        if (path.Count <= 1)
        {
            return;
        }

        string[] parent = [.. path.Take(path.Count - 1)];
        if (!await DirectoryExistsAsync(connection, parent, cancellationToken).ConfigureAwait(false))
        {
            await _client.PutAsync(connection, _bucket, FolderKey(parent), [], ifMatch: null, ifNoneMatch: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<List<string>> AllKeysAsync(S3Connection connection, string folder, CancellationToken cancellationToken)
    {
        List<string> keys = [];
        string? token = null;
        do
        {
            S3ListPage page = await _client.ListAsync(connection, _bucket, folder, delimiter: null, ListPageSize, token, cancellationToken).ConfigureAwait(false);
            keys.AddRange(page.Objects.Select(static item => item.Key));
            token = page.NextContinuationToken;
        }
        while (token is not null);

        return keys;
    }

    private string Key(IReadOnlyList<string> path) => _prefix + string.Join('/', path);

    private string FolderKey(IReadOnlyList<string> path) => path.Count == 0 ? _prefix : Key(path) + "/";

    private static HostFolderEntry File(string name, S3Object item) =>
        new(name, HostFolderEntryKind.File, item.Size, item.LastModifiedUtc ?? DateTimeOffset.UnixEpoch) { Revision = item.ETag };

    private static void RequireEntry(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "s3: the root of the drive is a folder");
        }

        foreach (string segment in path)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains('/', StringComparison.Ordinal))
            {
                throw new HostFolderException(HostFolderErrorKind.InvalidName, $"s3: '{segment}' is not a name");
            }
        }
    }
}
