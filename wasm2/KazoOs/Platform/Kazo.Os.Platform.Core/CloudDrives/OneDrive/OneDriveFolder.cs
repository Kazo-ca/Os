using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.OneDrive;

/// <summary>
/// One OneDrive drive (ADR 0267 §6): an <see cref="IHostFolder"/> over Microsoft Graph, at the
/// root of the account's drive (<c>Files.ReadWrite</c>) or of its app folder
/// (<c>Files.ReadWrite.AppFolder</c>, <c>Apps/Kazo OS</c>), optionally below a folder the user chose.
/// </summary>
/// <remarks>
/// <para>
/// Items are addressed by path (<c>/me/drive/root:/a/b:</c>). The revision is Graph's <c>eTag</c>;
/// a conditional write sends it as <c>If-Match</c> and a <c>412</c> becomes
/// <see cref="HostFolderErrorKind.RevisionConflict"/>. Content is downloaded from
/// <c>@microsoft.graph.downloadUrl</c>, never <c>/content</c>, whose redirection breaks CORS.
/// Up to <see cref="SimpleUploadLimit"/> a file is sent in one <c>PUT</c>; above, through an upload
/// session in <see cref="UploadChunkSize"/> pieces.
/// </para>
/// <para>
/// The permission and the token come from the drive's <see cref="CloudDriveConnection"/>: a OneDrive
/// sign-in lasts 24 hours (a single-page app's refresh token), then the drive asks for
/// "Reconnect", which opens Microsoft's window again.
/// </para>
/// </remarks>
public sealed class OneDriveFolder : IHostFolder, IHostFolderQuota, IConditionalHostFolder
{
    /// <summary>The largest file read or written whole in memory (ADR 0245, ADR 0267 §10): 128 MiB.</summary>
    public const long MaxFileBytes = 128L * 1024 * 1024;

    /// <summary>The largest file sent in a single <c>PUT</c>; larger ones go through an upload session.</summary>
    public const int SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>The size of an upload session's piece: a multiple of 320 KiB, as Graph requires.</summary>
    public const int UploadChunkSize = 32 * 320 * 1024;

    /// <summary>The most pages of a folder read before giving up (bounds a misbehaving <c>@odata.nextLink</c> chain).</summary>
    private const int MaxListPages = 1_000;

    private static readonly char[] ForbiddenNameCharacters = ['"', '*', ':', '<', '>', '?', '/', '\\', '|'];

    private readonly CloudDriveConnection _connection;
    private readonly OneDriveGraphClient _graph;
    private readonly string _driveBase;
    private readonly string[] _root;

    /// <summary>Initializes a drive.</summary>
    /// <param name="connection">The drive's link to its account (token, renewal, network failures).</param>
    /// <param name="http">Client of the pre-authenticated download and upload addresses; a fake handler in the tests.</param>
    /// <param name="name">Name shown as the drive's source (the account's name), as <c>onedrive:&lt;name&gt;</c> in <c>/proc/mounts</c>.</param>
    /// <param name="scope">The account's access: the whole drive or its app folder.</param>
    /// <param name="root">Folder of the drive the mount starts at; empty for the top.</param>
    public OneDriveFolder(CloudDriveConnection connection, HttpClient http, string name, CloudAccessScope scope, IReadOnlyList<string> root)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(root);
        _driveBase = scope == CloudAccessScope.AppFolder ? "me/drive/special/approot" : "me/drive/root";
        _root = [.. root];
        Name = string.IsNullOrEmpty(name) ? "OneDrive" : name;
        _graph = new OneDriveGraphClient(connection, http);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether the account can be used now, without the user and without Graph (<see cref="CloudDriveConnection.QueryPermissionAsync"/>).</summary>
    public ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default) =>
        _connection.QueryPermissionAsync(cancellationToken);

    /// <summary>"Reconnect": opens Microsoft's sign-in window when the account needs it (24 hours after the last sign-in), in the user's click.</summary>
    public ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        _connection.RequestPermissionAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        using JsonDocument item = await GetItemAsync(path, cancellationToken).ConfigureAwait(false);
        return ToEntry(item.RootElement, path.Count == 0 ? Name : null)
            ?? throw new HostFolderException(HostFolderErrorKind.NotFound, "This OneDrive item is neither a file nor a folder.");
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        List<HostFolderEntry> entries = [];
        string? next = Children(path);
        for (int page = 0; next is not null; page++)
        {
            if (page == MaxListPages)
            {
                throw new HostFolderException(HostFolderErrorKind.Failure, "This OneDrive folder has too many pages of entries.");
            }

            JsonDocument document;
            try
            {
                document = await GetJsonAsync(next, path, cancellationToken).ConfigureAwait(false);
            }
            catch (HostFolderException exception) when (page == 0 && exception.Kind is HostFolderErrorKind.Failure or HostFolderErrorKind.InvalidName)
            {
                // Graph refuses the children of a file: say so as the OS expects.
                await RequireFolderAsync(path, cancellationToken).ConfigureAwait(false);
                throw;
            }

            using JsonDocument owned = document;
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("value", out JsonElement values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in values.EnumerateArray())
                {
                    if (ToEntry(value, null) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
            }

            next = root.TryGetProperty("@odata.nextLink", out JsonElement link) && link.ValueKind == JsonValueKind.String
                ? link.GetString()
                : null;
            if (next is not null && !IsGraphAddress(next))
            {
                // The token goes with every page: never to another host than Graph.
                throw new HostFolderException(HostFolderErrorKind.Failure, "OneDrive sent a next page outside Microsoft Graph.");
            }
        }

        if (entries.Count == 0)
        {
            // An empty answer may come from a file: Graph has no children for it.
            await RequireFolderAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return entries;
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        string download;
        using (JsonDocument item = await GetItemAsync(path, cancellationToken).ConfigureAwait(false))
        {
            JsonElement root = item.RootElement;
            if (!root.TryGetProperty("file", out _))
            {
                throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "This OneDrive item is a folder.");
            }

            if (Size(root) > MaxFileBytes)
            {
                throw new HostFolderException(HostFolderErrorKind.FileTooLarge, "This OneDrive file is larger than 128 MiB.");
            }

            download = root.TryGetProperty("@microsoft.graph.downloadUrl", out JsonElement url) && url.GetString() is { Length: > 0 } text
                ? text
                : throw new HostFolderException(HostFolderErrorKind.Failure, "OneDrive gave no download address for this file.");
        }

        using HttpResponseMessage response = await _graph
            .SendPreauthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, download), cancellationToken)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxFileBytes)
        {
            throw new HostFolderException(HostFolderErrorKind.FileTooLarge, "This OneDrive file is larger than 128 MiB.");
        }

        return await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default)
    {
        string name = LastName(path);
        IReadOnlyList<string> parent = [.. path.Take(path.Count - 1)];
        if (kind == HostFolderEntryKind.File)
        {
            // A PUT by path creates the missing folders on the way: the parent must exist first,
            // as on any file system.
            await RequireFolderAsync(parent, cancellationToken).ConfigureAwait(false);
            using HttpResponseMessage created = await _graph.SendGraphAsync(
                HttpMethod.Put,
                $"{Item(path)}/content?@microsoft.graph.conflictBehavior=fail",
                static () => new ByteArrayContent([]),
                ifMatch: null,
                naming: true,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string body = new JsonObject
        {
            ["name"] = name,
            ["folder"] = new JsonObject(),
            ["@microsoft.graph.conflictBehavior"] = "fail"
        }.ToJsonString();
        using HttpResponseMessage response = await _graph.SendGraphAsync(
            HttpMethod.Post,
            Children(parent),
            () => Json(body),
            ifMatch: null,
            naming: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default) =>
        UploadAsync(path, content, expectedRevision: null, cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteIfMatchAsync(
        IReadOnlyList<string> path,
        byte[] content,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedRevision);
        return UploadAsync(path, content, expectedRevision, cancellationToken);
    }

    /// <summary>Deletes an entry; OneDrive keeps it in its recycle bin.</summary>
    public async ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.NotAllowed, "The root of the drive cannot be deleted.");
        }

        if (!recursive)
        {
            using JsonDocument item = await GetItemAsync(path, cancellationToken).ConfigureAwait(false);
            if (item.RootElement.TryGetProperty("folder", out JsonElement folder)
                && folder.TryGetProperty("childCount", out JsonElement count)
                && count.TryGetInt64(out long children)
                && children > 0)
            {
                throw new HostFolderException(HostFolderErrorKind.NotEmpty, "This OneDrive folder is not empty.");
            }
        }

        using HttpResponseMessage response = await _graph
            .SendGraphAsync(HttpMethod.Delete, Item(path), null, ifMatch: null, naming: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Moves or renames an entry in one Graph request (<c>PATCH</c> of its name and parent).</summary>
    public async ValueTask<bool> TryMoveAsync(
        IReadOnlyList<string> source,
        IReadOnlyList<string> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        string name = LastName(destination);
        if (source.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.NotAllowed, "The root of the drive cannot be moved.");
        }

        JsonObject body = new() { ["name"] = name };
        IReadOnlyList<string> sourceParent = [.. source.Take(source.Count - 1)];
        IReadOnlyList<string> destinationParent = [.. destination.Take(destination.Count - 1)];
        if (!sourceParent.SequenceEqual(destinationParent, StringComparer.Ordinal))
        {
            using JsonDocument parent = await GetItemAsync(destinationParent, cancellationToken).ConfigureAwait(false);
            if (!parent.RootElement.TryGetProperty("folder", out _) || !parent.RootElement.TryGetProperty("id", out JsonElement id))
            {
                throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "The destination's parent is not a OneDrive folder.");
            }

            body["parentReference"] = new JsonObject { ["id"] = id.GetString() };
        }

        string text = body.ToJsonString();
        using HttpResponseMessage response = await _graph.SendGraphAsync(
            HttpMethod.Patch,
            Item(source),
            () => Json(text),
            ifMatch: null,
            naming: true,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Asks Graph for the drive's quota (<c>/me/drive</c>); <see langword="null"/> when the access does not allow it.</summary>
    public async ValueTask<HostFolderQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using JsonDocument drive = await _graph
                .GetJsonAsync(HttpMethod.Get, "me/drive?$select=quota", null, null, naming: false, cancellationToken)
                .ConfigureAwait(false);
            if (!drive.RootElement.TryGetProperty("quota", out JsonElement quota)
                || !quota.TryGetProperty("total", out JsonElement totalValue)
                || !totalValue.TryGetInt64(out long total)
                || total <= 0)
            {
                return null;
            }

            long free = quota.TryGetProperty("remaining", out JsonElement remainingValue) && remainingValue.TryGetInt64(out long remaining)
                ? Math.Clamp(remaining, 0, total)
                : 0;
            return new HostFolderQuota(total, free);
        }
        catch (HostFolderException exception) when (exception.Kind is HostFolderErrorKind.NotAllowed or HostFolderErrorKind.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Checks that <paramref name="path"/> is a folder of the drive.</summary>
    /// <exception cref="HostFolderException"><see cref="HostFolderErrorKind.NotFound"/> or <see cref="HostFolderErrorKind.TypeMismatch"/>.</exception>
    private async ValueTask RequireFolderAsync(IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        using JsonDocument item = await GetItemAsync(path, cancellationToken).ConfigureAwait(false);
        if (!item.RootElement.TryGetProperty("folder", out _))
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "This OneDrive item is a file, not a folder.");
        }
    }

    private async ValueTask UploadAsync(IReadOnlyList<string> path, byte[] content, string? expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        LastName(path);
        if (content.LongLength > MaxFileBytes)
        {
            throw new HostFolderException(HostFolderErrorKind.FileTooLarge, "A file larger than 128 MiB cannot be written.");
        }

        if (content.Length <= SimpleUploadLimit)
        {
            using HttpResponseMessage response = await _graph.SendGraphAsync(
                HttpMethod.Put,
                $"{Item(path)}/content",
                () => new ByteArrayContent(content),
                expectedRevision,
                naming: true,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await UploadInSessionAsync(path, content, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a large file through an upload session, piece by piece, to its pre-authenticated address.</summary>
    private async ValueTask UploadInSessionAsync(IReadOnlyList<string> path, byte[] content, string? expectedRevision, CancellationToken cancellationToken)
    {
        string body = new JsonObject
        {
            ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = "replace" }
        }.ToJsonString();
        string uploadUrl;
        using (JsonDocument session = await _graph.GetJsonAsync(
            HttpMethod.Post,
            $"{Item(path)}/createUploadSession",
            () => Json(body),
            expectedRevision,
            naming: true,
            cancellationToken).ConfigureAwait(false))
        {
            uploadUrl = session.RootElement.TryGetProperty("uploadUrl", out JsonElement url) && url.GetString() is { Length: > 0 } text
                ? text
                : throw new HostFolderException(HostFolderErrorKind.Failure, "OneDrive gave no upload address.");
        }

        try
        {
            for (int offset = 0; offset < content.Length; offset += UploadChunkSize)
            {
                int start = offset;
                int length = Math.Min(UploadChunkSize, content.Length - start);
                using HttpResponseMessage response = await _graph.SendPreauthenticatedAsync(
                    () =>
                    {
                        ByteArrayContent piece = new(content, start, length);
                        piece.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, content.Length);
                        return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = piece };
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await CancelSessionAsync(uploadUrl).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Drops an unfinished upload session; a failure here changes nothing for the user.</summary>
    private async ValueTask CancelSessionAsync(string uploadUrl)
    {
        try
        {
            using HttpResponseMessage response = await _graph
                .SendPreauthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Delete, uploadUrl), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (HostFolderException)
        {
            // The session expires by itself after a while.
        }
    }

    /// <summary>
    /// Gets the item at <paramref name="path"/>. OneDrive finds names whatever their case; Kazo OS
    /// does not, so an item whose name differs from the one asked is reported missing, as on a
    /// case-sensitive file system.
    /// </summary>
    private async ValueTask<JsonDocument> GetItemAsync(IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        JsonDocument item = await GetJsonAsync(Item(path), path, cancellationToken).ConfigureAwait(false);
        if (path.Count > 0
            && item.RootElement.TryGetProperty("name", out JsonElement nameValue)
            && nameValue.GetString() is { } name
            && !string.Equals(name, path[^1], StringComparison.Ordinal))
        {
            item.Dispose();
            throw new HostFolderException(HostFolderErrorKind.NotFound, $"OneDrive has '{name}', not '{path[^1]}'.");
        }

        return item;
    }

    private async ValueTask<JsonDocument> GetJsonAsync(string address, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await _graph.GetJsonAsync(HttpMethod.Get, address, null, null, naming: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Graph address of the item at <paramref name="path"/>, below the drive's root.</summary>
    private string Item(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string[] segments = [.. _root, .. path];
        return segments.Length == 0
            ? _driveBase
            : $"{_driveBase}:/{string.Join('/', segments.Select(Uri.EscapeDataString))}:";
    }

    /// <summary>The Graph address of the children of the folder at <paramref name="path"/>.</summary>
    private string Children(IReadOnlyList<string> path) =>
        _root.Length + path.Count == 0 ? $"{_driveBase}/children" : $"{Item(path)}/children";

    /// <summary>The last segment of a path that names an entry, checked against the names OneDrive refuses.</summary>
    private static string LastName(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new HostFolderException(HostFolderErrorKind.TypeMismatch, "The root of the drive is a folder.");
        }

        string name = path[^1];
        if (name.Length == 0
            || name.IndexOfAny(ForbiddenNameCharacters) >= 0
            || char.IsWhiteSpace(name[0])
            || char.IsWhiteSpace(name[^1])
            || name[^1] == '.')
        {
            throw new HostFolderException(HostFolderErrorKind.InvalidName, $"OneDrive refuses the name '{name}'.");
        }

        return name;
    }

    /// <summary>Describes a Graph <c>driveItem</c>; <see langword="null"/> for what is neither a file nor a folder (a OneNote package).</summary>
    private static HostFolderEntry? ToEntry(JsonElement item, string? nameOverride)
    {
        string? name = nameOverride ?? (item.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() : null);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (item.TryGetProperty("folder", out _))
        {
            return new HostFolderEntry(name, HostFolderEntryKind.Directory, 0, null);
        }

        if (!item.TryGetProperty("file", out _))
        {
            return null;
        }

        DateTimeOffset? modified = item.TryGetProperty("lastModifiedDateTime", out JsonElement date)
            && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;
        string? revision = item.TryGetProperty("eTag", out JsonElement tag) ? tag.GetString() : null;
        return new HostFolderEntry(name, HostFolderEntryKind.File, Math.Max(0, Size(item)), modified) { Revision = revision };
    }

    private static bool IsGraphAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, OneDriveGraphClient.GraphBase.Host, StringComparison.OrdinalIgnoreCase);

    private static long Size(JsonElement item) =>
        item.TryGetProperty("size", out JsonElement size) && size.TryGetInt64(out long bytes) ? bytes : 0;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Reads a download, refusing more than <see cref="MaxFileBytes"/> even when its size was not announced.</summary>
    private static async ValueTask<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
            {
                throw new HostFolderException(HostFolderErrorKind.FileTooLarge, "This OneDrive file is larger than 128 MiB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
