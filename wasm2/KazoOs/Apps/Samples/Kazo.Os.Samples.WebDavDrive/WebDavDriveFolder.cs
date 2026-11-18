using System.Net.Http;
using System.Text;
using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.Samples.WebDavDrive;

/// <summary>
/// One WebDAV drive (RFC 4918): each operation of <see cref="KazoDriveFolder"/> is one or two
/// requests to the server, signed with HTTP Basic from the user name and the sealed password.
/// </summary>
/// <remarks>
/// A file's revision is its <c>ETag</c>: a conditional write sends <c>If-Match</c>, a create sends
/// <c>If-None-Match: *</c>, so a file another device changed is never replaced (ADR 0267 §4).
/// </remarks>
public sealed class WebDavDriveFolder : KazoDriveFolder
{
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    private readonly IWebDavTransport _transport;
    private readonly IKazoDriveProviderContext _context;
    private readonly Uri _origin;
    private readonly string[] _rootSegments;
    private readonly string _rootPath;
    private readonly string _userName;

    /// <summary>Creates the drive; nothing is sent to the server here.</summary>
    /// <param name="transport">How requests are sent.</param>
    /// <param name="context">The context the OS lends the drive (the sealed password).</param>
    /// <param name="server">The server's WebDAV folder, absolute <c>https</c> (<c>https://cloud.example.org/remote.php/dav/files/ben/</c>).</param>
    /// <param name="root">The folder under <paramref name="server"/> the drive starts at, or <see langword="null"/>.</param>
    /// <param name="userName">The user name of HTTP Basic.</param>
    public WebDavDriveFolder(IWebDavTransport transport, IKazoDriveProviderContext context, Uri server, KazoDrivePath? root, string userName)
    {
        ArgumentNullException.ThrowIfNull(server);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _userName = userName ?? throw new ArgumentNullException(nameof(userName));
        _origin = new Uri(server.GetLeftPart(UriPartial.Authority));
        _rootSegments = [.. server.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString), .. root?.Segments ?? []];
        _rootPath = "/" + string.Join('/', _rootSegments);
    }

    /// <inheritdoc />
    public override async ValueTask<KazoDriveState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await StatAsync(KazoDrivePath.Root, cancellationToken).ConfigureAwait(false);
            return KazoDriveState.Ready;
        }
        catch (KazoDriveException exception) when (exception.Kind == KazoDriveErrorKind.SignInRequired)
        {
            return KazoDriveState.SignInRequired;
        }
        catch (KazoDriveException)
        {
            // Down, or the address no longer leads to the folder: the drive waits disconnected.
            return KazoDriveState.Unavailable;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WebDavResource> found = await PropfindAsync(path, depth: "0", WebDavMultistatus.EntryProperties, cancellationToken).ConfigureAwait(false);
        WebDavResource self = found.FirstOrDefault(resource => resource.Path == PathOf(path))
            ?? found.FirstOrDefault()
            ?? throw new KazoDriveException(KazoDriveErrorKind.Other, "The server described nothing at the path.");
        return ToEntry(path.Name, self);
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        string self = PathOf(path);
        IReadOnlyList<WebDavResource> found = await PropfindAsync(path, depth: "1", WebDavMultistatus.EntryProperties, cancellationToken).ConfigureAwait(false);
        if (found.FirstOrDefault(resource => resource.Path == self) is { IsCollection: false })
        {
            throw new KazoDriveException(KazoDriveErrorKind.TypeMismatch, "The path is a file.");
        }

        List<KazoDriveEntry> entries = [];
        foreach (WebDavResource resource in found)
        {
            // Only the direct children; a name the OS could not hold (a separator) is left out.
            int slash = resource.Path.LastIndexOf('/');
            string parent = slash <= 0 ? "/" : resource.Path[..slash];
            string name = resource.Path[(slash + 1)..];
            if (resource.Path != self && parent == self && KazoDrivePath.IsValidName(name))
            {
                entries.Add(ToEntry(name, resource));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public override async ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        KazoDriveEntry entry = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry.Kind == KazoDriveEntryKind.Folder)
        {
            throw new KazoDriveException(KazoDriveErrorKind.TypeMismatch, "The path is a folder.");
        }

        if (entry.Length > KazoDriveLimits.MaxFileBytes)
        {
            throw new KazoDriveException(KazoDriveErrorKind.FileTooLarge, "The file is larger than 128 MiB.");
        }

        await using WebDavResponse response = await SendAsync(HttpMethod.Get, path, isFolder: false, [], null, cancellationToken).ConfigureAwait(false);
        ThrowOnError(response.Status);
        if (long.TryParse(response.Header("Content-Length"), out long length) && length > KazoDriveLimits.MaxFileBytes)
        {
            throw new KazoDriveException(KazoDriveErrorKind.FileTooLarge, "The file is larger than 128 MiB.");
        }

        return await ReadBoundedAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken cancellationToken = default)
    {
        KeyValuePair<string, string>[] condition = ifRevision is null ? [] : [new("If-Match", ifRevision)];
        await using WebDavResponse response = await SendAsync(HttpMethod.Put, path, isFolder: false, condition, content, cancellationToken).ConfigureAwait(false);
        switch (response.Status)
        {
            case 412:
                throw new KazoDriveException(KazoDriveErrorKind.RevisionConflict, "The file changed on the server since it was read.");
            case 409:
                throw new KazoDriveException(KazoDriveErrorKind.NotFound, "The folder of the file does not exist.");
            case 405:
                throw new KazoDriveException(KazoDriveErrorKind.TypeMismatch, "The path is a folder.");
            default:
                ThrowOnError(response.Status);
                break;
        }
    }

    /// <inheritdoc />
    public override async ValueTask CreateFileAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        await using WebDavResponse response = await SendAsync(
            HttpMethod.Put, path, isFolder: false, [new("If-None-Match", "*")], ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        switch (response.Status)
        {
            case 412 or 405:
                throw new KazoDriveException(KazoDriveErrorKind.Exists, "An entry already has the name.");
            case 409:
                throw new KazoDriveException(KazoDriveErrorKind.NotFound, "The folder of the file does not exist.");
            default:
                ThrowOnError(response.Status);
                break;
        }
    }

    /// <inheritdoc />
    public override async ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        await using WebDavResponse response = await SendAsync(Mkcol, path, isFolder: true, [], null, cancellationToken).ConfigureAwait(false);
        switch (response.Status)
        {
            case 405:
                throw new KazoDriveException(KazoDriveErrorKind.Exists, "An entry already has the name.");
            case 409:
                throw new KazoDriveException(KazoDriveErrorKind.NotFound, "The parent folder does not exist.");
            default:
                ThrowOnError(response.Status);
                break;
        }
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken cancellationToken = default)
    {
        // A WebDAV DELETE of a collection always takes its content (RFC 4918 §9.6.1): check first.
        KazoDriveEntry entry = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (!recursive && entry.Kind == KazoDriveEntryKind.Folder
            && (await ListAsync(path, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new KazoDriveException(KazoDriveErrorKind.NotEmpty, "The folder has entries.");
        }

        bool folder = entry.Kind == KazoDriveEntryKind.Folder;
        await using WebDavResponse response = await SendAsync(HttpMethod.Delete, path, folder, [], null, cancellationToken).ConfigureAwait(false);
        ThrowOnError(response.Status);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> TryMoveAsync(KazoDrivePath from, KazoDrivePath to, CancellationToken cancellationToken = default)
    {
        KazoDriveEntry entry = await StatAsync(from, cancellationToken).ConfigureAwait(false);
        bool folder = entry.Kind == KazoDriveEntryKind.Folder;
        KeyValuePair<string, string>[] headers = [new("Destination", UriOf(to, folder).AbsoluteUri), new("Overwrite", "F")];
        await using WebDavResponse response = await SendAsync(Move, from, folder, headers, null, cancellationToken).ConfigureAwait(false);
        switch (response.Status)
        {
            case 412:
                throw new KazoDriveException(KazoDriveErrorKind.Exists, "An entry already has the new name.");
            case 409:
                throw new KazoDriveException(KazoDriveErrorKind.NotFound, "The new folder does not exist.");
            case 403 or 501 or 502:
                // The server will not move it (another server, a forbidden name): the OS copies and deletes.
                return false;
            default:
                ThrowOnError(response.Status);
                return true;
        }
    }

    /// <inheritdoc />
    public override async ValueTask<KazoDriveQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WebDavResource> found = await PropfindAsync(KazoDrivePath.Root, "0", WebDavMultistatus.QuotaProperties, cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault() is { QuotaAvailable: long free and >= 0, QuotaUsed: long used and >= 0 }
            ? new KazoDriveQuota(checked(free + used), free)
            : null;
    }

    private async Task<IReadOnlyList<WebDavResource>> PropfindAsync(KazoDrivePath path, string depth, byte[] body, CancellationToken cancellationToken)
    {
        KeyValuePair<string, string>[] headers = [new("Depth", depth)];
        await using WebDavResponse response = await SendAsync(Propfind, path, isFolder: depth != "0" || path.IsRoot, headers, body, cancellationToken, "application/xml; charset=utf-8")
            .ConfigureAwait(false);
        ThrowOnError(response.Status);
        if (response.Status != 207)
        {
            throw new KazoDriveException(KazoDriveErrorKind.Other, $"The server answered PROPFIND with {response.Status}, not 207: is it a WebDAV server?");
        }

        try
        {
            return await WebDavMultistatus.ParseAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            throw new KazoDriveException(KazoDriveErrorKind.Other, exception.Message, exception);
        }
    }

    private async Task<WebDavResponse> SendAsync(
        HttpMethod method,
        KazoDrivePath path,
        bool isFolder,
        KeyValuePair<string, string>[] headers,
        ReadOnlyMemory<byte>? body,
        CancellationToken cancellationToken,
        string contentType = "application/octet-stream")
    {
        string? password = await _context.GetSecretAsync(WebDavFields.Password, cancellationToken).ConfigureAwait(false);
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_userName}:{password}"));
        WebDavRequest request = new(method, UriOf(path, isFolder), [new("Authorization", "Basic " + basic), .. headers], body, contentType);
        try
        {
            return await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // A network failure, a CORS refusal and a TLS error look the same to a page.
            throw new KazoDriveException(KazoDriveErrorKind.Unavailable, "The WebDAV server cannot be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KazoDriveException(KazoDriveErrorKind.Unavailable, "The WebDAV server did not answer in time.", exception);
        }
    }

    private static void ThrowOnError(int status)
    {
        KazoDriveErrorKind? kind = status switch
        {
            >= 200 and <= 299 => null,
            401 => KazoDriveErrorKind.SignInRequired,
            403 => KazoDriveErrorKind.AccessDenied,
            404 or 410 => KazoDriveErrorKind.NotFound,
            507 => KazoDriveErrorKind.QuotaExceeded,
            408 or 429 or >= 500 => KazoDriveErrorKind.Unavailable,
            _ => KazoDriveErrorKind.Other,
        };
        if (kind is { } error)
        {
            throw new KazoDriveException(error, $"The WebDAV server answered {status}.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > KazoDriveLimits.MaxFileBytes)
            {
                throw new KazoDriveException(KazoDriveErrorKind.FileTooLarge, "The file is larger than 128 MiB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static KazoDriveEntry ToEntry(string name, WebDavResource resource) =>
        new(name, resource.IsCollection ? KazoDriveEntryKind.Folder : KazoDriveEntryKind.File, resource.Length, resource.LastModified)
        {
            Revision = resource.ETag
        };

    /// <summary>The decoded server path of <paramref name="path"/>, as <see cref="WebDavMultistatus.DecodePath"/> writes it.</summary>
    private string PathOf(KazoDrivePath path) =>
        path.IsRoot ? _rootPath : (_rootPath == "/" ? string.Empty : _rootPath) + "/" + string.Join('/', path.Segments);

    /// <summary>The address of <paramref name="path"/>: each name escaped, a <c>/</c> after a folder.</summary>
    private Uri UriOf(KazoDrivePath path, bool isFolder)
    {
        IEnumerable<string> segments = _rootSegments.Concat(path.Segments).Select(Uri.EscapeDataString);
        string encoded = "/" + string.Join('/', segments);
        if (isFolder && !encoded.EndsWith('/'))
        {
            encoded += "/";
        }

        return new Uri(_origin, encoded);
    }
}
