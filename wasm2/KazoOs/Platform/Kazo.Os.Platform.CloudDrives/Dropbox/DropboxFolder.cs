using System.Security.Cryptography;
using System.Text.Json;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.Dropbox;

/// <summary>
/// A Dropbox drive (ADR 0267 §6): an <see cref="IHostFolder"/> over the Dropbox API v2, with the
/// quota of <c>users/get_space_usage</c> and conditional writes on the file's <c>rev</c>
/// (<c>files/upload</c> in <c>update</c> mode).
/// </summary>
/// <remarks>
/// <para>
/// <b>Case.</b> Dropbox names are case-insensitive and keep the case they were created with; Kazo OS
/// paths are case-sensitive. A path whose last name differs from Dropbox's only by case is "not
/// found", so the OS sees one entry, under its real name; creating <c>a.txt</c> next to <c>A.txt</c>
/// is "already exists". A rename that changes only the case goes through a temporary name, because
/// Dropbox refuses it in one move.
/// </para>
/// <para>
/// <b>Writes.</b> Each file is sent whole by the simple upload (150 MB at most; the OS stops at
/// 128 MiB, ADR 0245). Creating a file or folder checks its parent first, because Dropbox would
/// create missing parents where a folder of the computer refuses.
/// </para>
/// </remarks>
public sealed class DropboxFolder : IHostFolder, IHostFolderQuota, IConditionalHostFolder
{
    private readonly CloudDriveConnection _connection;
    private readonly string? _root;

    /// <summary>Initializes the folder.</summary>
    /// <param name="connection">The link to the account.</param>
    /// <param name="name">Name shown as the drive's source (the account's name).</param>
    /// <param name="root">The Dropbox folder the drive starts at, normalized (<c>/a/b</c>); <see langword="null"/> for everything the account reaches.</param>
    public DropboxFolder(CloudDriveConnection connection, string name, string? root)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Name = string.IsNullOrEmpty(name) ? "Dropbox" : name;
        _root = DropboxPaths.NormalizeRoot(root);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Gets the Dropbox folder the drive starts at; <see langword="null"/> for everything the account reaches.</summary>
    public string? Root => _root;

    /// <inheritdoc />
    public ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default) =>
        _connection.QueryPermissionAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        _connection.RequestPermissionAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0 && _root is null)
        {
            // Dropbox has no metadata for the root of the account.
            return new HostFolderEntry(Name, HostFolderEntryKind.Directory, 0, null);
        }

        string dropboxPath = DropboxPaths.Combine(_root, path);
        using JsonDocument document = await CallAsync(
            "files/get_metadata",
            writer => writer.WriteString("path", dropboxPath),
            HostFolderErrorKind.AlreadyExists,
            cancellationToken).ConfigureAwait(false);
        HostFolderEntry entry = DropboxApi.ReadEntry(document.RootElement)
            ?? throw new HostFolderException(HostFolderErrorKind.NotFound, $"{dropboxPath}: not found");
        if (path.Count > 0 && !string.Equals(entry.Name, path[^1], StringComparison.Ordinal))
        {
            throw new HostFolderException(HostFolderErrorKind.NotFound, $"{dropboxPath}: not found (Dropbox has '{entry.Name}')");
        }

        return entry;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        string dropboxPath = DropboxPaths.Combine(_root, path);
        if (path.Count > 0)
        {
            // Dropbox would list "a" for "A": check the exact name first.
            HostFolderEntry folder = await StatAsync(path, cancellationToken).ConfigureAwait(false);
            if (folder.Kind != HostFolderEntryKind.Directory)
            {
                throw new HostFolderException(HostFolderErrorKind.TypeMismatch, $"{dropboxPath}: not a folder");
            }
        }

        List<HostFolderEntry> entries = [];
        JsonDocument page = await CallAsync(
            "files/list_folder",
            writer =>
            {
                writer.WriteString("path", dropboxPath);
                writer.WriteBoolean("recursive", false);
                writer.WriteBoolean("include_deleted", false);
                writer.WriteNumber("limit", DropboxApi.ListPageSize);
            },
            HostFolderErrorKind.AlreadyExists,
            cancellationToken).ConfigureAwait(false);
        while (true)
        {
            string? cursor;
            bool more;
            using (page)
            {
                JsonElement root = page.RootElement;
                if (root.TryGetProperty("entries", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        if (DropboxApi.ReadEntry(item) is { } entry)
                        {
                            entries.Add(entry);
                        }
                    }
                }

                more = root.TryGetProperty("has_more", out JsonElement hasMore) && hasMore.ValueKind == JsonValueKind.True;
                cursor = root.TryGetProperty("cursor", out JsonElement cursorElement) ? cursorElement.GetString() : null;
            }

            if (!more || string.IsNullOrEmpty(cursor))
            {
                return entries;
            }

            page = await CallAsync(
                "files/list_folder/continue",
                writer => writer.WriteString("cursor", cursor),
                HostFolderErrorKind.AlreadyExists,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        await RequireFileAsync(path, cancellationToken).ConfigureAwait(false);
        string dropboxPath = DropboxPaths.Combine(_root, path);
        using HttpResponseMessage response = await _connection.SendAsync(
            () => DropboxApi.Content("files/download", writer => writer.WriteString("path", dropboxPath), upload: null),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await DropboxApi.ErrorAsync(response, HostFolderErrorKind.AlreadyExists, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.AlreadyExists, "The root of the drive already exists.");
        }

        string dropboxPath = DropboxPaths.Combine(_root, path);
        await RequireParentAsync(path, cancellationToken).ConfigureAwait(false);
        if (kind == HostFolderEntryKind.Directory)
        {
            using JsonDocument created = await CallAsync(
                "files/create_folder_v2",
                writer =>
                {
                    writer.WriteString("path", dropboxPath);
                    writer.WriteBoolean("autorename", false);
                },
                HostFolderErrorKind.AlreadyExists,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await UploadAsync(dropboxPath, [], writer => writer.WriteString("mode", "add"), HostFolderErrorKind.AlreadyExists, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        string dropboxPath = WritablePath(path, content);
        await UploadAsync(dropboxPath, content, writer => writer.WriteString("mode", "overwrite"), HostFolderErrorKind.AlreadyExists, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Uploads in <c>update</c> mode: Dropbox refuses when the file's <c>rev</c> is no longer <paramref name="expectedRevision"/>.</summary>
    public async ValueTask WriteIfMatchAsync(
        IReadOnlyList<string> path,
        byte[] content,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(expectedRevision);
        string dropboxPath = WritablePath(path, content);
        await UploadAsync(
            dropboxPath,
            content,
            writer =>
            {
                writer.WriteStartObject("mode");
                writer.WriteString(".tag", "update");
                writer.WriteString("update", expectedRevision);
                writer.WriteEndObject();
            },
            HostFolderErrorKind.RevisionConflict,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.NotAllowed, "The root of the drive cannot be deleted.");
        }

        HostFolderEntry entry = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (!recursive && entry.Kind == HostFolderEntryKind.Directory
            && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            // Dropbox always deletes a folder with its content: refuse here, as the browser does.
            throw new HostFolderException(HostFolderErrorKind.NotEmpty, $"{entry.Name}: the folder is not empty");
        }

        string dropboxPath = DropboxPaths.Combine(_root, path);
        using JsonDocument deleted = await CallAsync(
            "files/delete_v2",
            writer => writer.WriteString("path", dropboxPath),
            HostFolderErrorKind.AlreadyExists,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves or renames in one call (<c>files/move_v2</c>); a change of case alone takes two.</summary>
    public async ValueTask<bool> TryMoveAsync(
        IReadOnlyList<string> source,
        IReadOnlyList<string> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source.Count == 0 || destination.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.NotAllowed, "The root of the drive cannot be moved.");
        }

        string from = DropboxPaths.Combine(_root, source);
        string to = DropboxPaths.Combine(_root, destination);
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = $"{to}.kazoos-rename-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
            await MoveAsync(from, temporary, cancellationToken).ConfigureAwait(false);
            await MoveAsync(temporary, to, cancellationToken).ConfigureAwait(false);
            return true;
        }

        await MoveAsync(from, to, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The account's space: its allocation, and what is left of it.</summary>
    public async ValueTask<HostFolderQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument usage = await CallAsync("users/get_space_usage", body: null, HostFolderErrorKind.AlreadyExists, cancellationToken)
            .ConfigureAwait(false);
        JsonElement root = usage.RootElement;
        if (!root.TryGetProperty("used", out JsonElement usedElement) || !usedElement.TryGetInt64(out long used)
            || !root.TryGetProperty("allocation", out JsonElement allocation)
            || !allocation.TryGetProperty("allocated", out JsonElement allocatedElement) || !allocatedElement.TryGetInt64(out long allocated)
            || allocated <= 0)
        {
            return null;
        }

        return new HostFolderQuota(allocated, Math.Clamp(allocated - used, 0, allocated));
    }

    /// <summary>Nothing to release: the HTTP client belongs to the source.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask MoveAsync(string from, string to, CancellationToken cancellationToken)
    {
        using JsonDocument moved = await CallAsync(
            "files/move_v2",
            writer =>
            {
                writer.WriteString("from_path", from);
                writer.WriteString("to_path", to);
                writer.WriteBoolean("autorename", false);
                writer.WriteBoolean("allow_ownership_transfer", false);
            },
            HostFolderErrorKind.AlreadyExists,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UploadAsync(
        string dropboxPath,
        byte[] content,
        Action<Utf8JsonWriter> mode,
        HostFolderErrorKind conflict,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _connection.SendAsync(
            () => DropboxApi.Content(
                "files/upload",
                writer =>
                {
                    writer.WriteString("path", dropboxPath);
                    mode(writer);
                    writer.WriteBoolean("autorename", false);
                    writer.WriteBoolean("mute", true);
                    writer.WriteBoolean("strict_conflict", true);
                },
                content),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await DropboxApi.ErrorAsync(response, conflict, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<JsonDocument> CallAsync(
        string endpoint,
        Action<Utf8JsonWriter>? body,
        HostFolderErrorKind conflict,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _connection.SendAsync(() => DropboxApi.Rpc(endpoint, body), cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await DropboxApi.ErrorAsync(response, conflict, cancellationToken).ConfigureAwait(false);
        }

        return await DropboxApi.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RequireFileAsync(IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        HostFolderEntry entry = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry.Kind != HostFolderEntryKind.File)
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, $"{entry.Name}: not a file");
        }
    }

    private async ValueTask RequireParentAsync(IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        if (path.Count < 2)
        {
            return;
        }

        HostFolderEntry parent = await StatAsync([.. path.Take(path.Count - 1)], cancellationToken).ConfigureAwait(false);
        if (parent.Kind != HostFolderEntryKind.Directory)
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, $"{parent.Name}: not a folder");
        }
    }

    private string WritablePath(IReadOnlyList<string> path, byte[] content)
    {
        if (path.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "The root of the drive is a folder.");
        }

        if (content.LongLength > DropboxApi.MaxUploadBytes)
        {
            throw new HostFolderException(HostFolderErrorKind.Failure, "Dropbox takes files of 150 MB at most in one upload.");
        }

        return DropboxPaths.Combine(_root, path);
    }
}
