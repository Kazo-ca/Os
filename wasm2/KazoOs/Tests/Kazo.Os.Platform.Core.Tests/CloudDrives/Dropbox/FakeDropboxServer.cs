using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.Dropbox;

/// <summary>
/// An in-memory Dropbox API v2 behind an <see cref="HttpMessageHandler"/>: the RPC and content
/// endpoints the connector calls, Dropbox's case-insensitive names that keep their case, revisions
/// (<c>rev</c>), the <c>add</c>, <c>overwrite</c> and <c>update</c> upload modes, 409 errors with an
/// <c>error_summary</c>, the space usage, and the checks Dropbox makes on headers (an ASCII
/// <c>Dropbox-API-Arg</c>, a bare <c>application/json</c>). Nothing reaches the network.
/// </summary>
public sealed class FakeDropboxServer : HttpMessageHandler
{
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _revokedTokens = new(StringComparer.Ordinal);
    private readonly Func<string> _validToken;
    private int _revision = 0x1a2b3c;
    private int _failures;
    private int _failureStatus;

    /// <summary>Initializes the server.</summary>
    /// <param name="validToken">The access token the account currently has.</param>
    public FakeDropboxServer(Func<string> validToken)
    {
        _validToken = validToken;
        _nodes[string.Empty] = new Node(string.Empty, IsFolder: true, null, null, DateTimeOffset.UnixEpoch);
    }

    /// <summary>Gets or sets whether the network works.</summary>
    public bool Online { get; set; } = true;

    /// <summary>Gets or sets the bytes the account uses.</summary>
    public long Used { get; set; } = 1_234_567;

    /// <summary>Gets or sets the bytes allocated to the account.</summary>
    public long Allocated { get; set; } = 2_147_483_648;

    /// <summary>Gets or sets the size of a page of <c>files/list_folder</c>, whatever the client asks.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Gets each request: endpoint, argument (body or header) as JSON, content type.</summary>
    public List<(string Endpoint, JsonObject? Argument, string? ContentType, string? Authorization)> Requests { get; } = [];

    /// <summary>Creates a folder with its parents.</summary>
    public void PutFolder(string path)
    {
        string normalized = Normalize(path);
        if (normalized.Length > 0 && !_nodes.ContainsKey(normalized))
        {
            EnsureParents(normalized);
            _nodes[normalized] = new Node(normalized, IsFolder: true, null, null, DateTimeOffset.UnixEpoch);
        }
    }

    /// <summary>Creates or replaces a file with its parents.</summary>
    public void PutFile(string path, byte[] content)
    {
        string normalized = Normalize(path);
        EnsureParents(normalized);
        string display = _nodes.TryGetValue(normalized, out Node? existing) ? existing.Path : normalized;
        _nodes[normalized] = new Node(display, IsFolder: false, content, NextRev(), new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>Reads a file; <see langword="null"/> when absent.</summary>
    public byte[]? Read(string path) =>
        _nodes.TryGetValue(Normalize(path), out Node? node) && !node.IsFolder ? node.Content : null;

    /// <summary>Gets whether anything is at <paramref name="path"/> (any case).</summary>
    public bool Exists(string path) => _nodes.ContainsKey(Normalize(path));

    /// <summary>Gets the stored name of the entry at <paramref name="path"/> (any case).</summary>
    public string? DisplayPath(string path) => _nodes.TryGetValue(Normalize(path), out Node? node) ? node.Path : null;

    /// <summary>Refuses the account's current token from now on.</summary>
    public void RevokeCurrentToken() => _revokedTokens.Add(_validToken());

    /// <summary>The next <paramref name="count"/> requests answer <paramref name="status"/>.</summary>
    public void FailNext(int count, int status)
    {
        _failures = count;
        _failureStatus = status;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Online)
        {
            throw new HttpRequestException("TypeError: Failed to fetch");
        }

        string endpoint = request.RequestUri!.AbsolutePath.TrimStart('/')["2/".Length..];
        string? authorization = request.Headers.Authorization?.ToString();
        string? contentType = request.Content?.Headers.ContentType?.ToString();
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonObject? argument;
        bool contentEndpoint = request.RequestUri.Host == "content.dropboxapi.com";
        if (contentEndpoint)
        {
            string? header = request.Headers.TryGetValues("Dropbox-API-Arg", out IEnumerable<string>? values) ? values.Single() : null;
            if (header is null || header.Any(static character => character > 127))
            {
                return Status(HttpStatusCode.BadRequest, "Dropbox-API-Arg must be present and ASCII");
            }

            argument = JsonNode.Parse(header)!.AsObject();
        }
        else
        {
            if (body.Length > 0 && contentType != "application/json")
            {
                return Status(HttpStatusCode.BadRequest, $"Bad HTTP \"Content-Type\" header: \"{contentType}\"");
            }

            argument = body.Length == 0 ? null : JsonNode.Parse(body)!.AsObject();
        }

        Requests.Add((endpoint, argument, contentType, authorization));
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.BadRequest, "missing_authorization/");
        }

        string token = authorization["Bearer ".Length..];
        if (_revokedTokens.Contains(token) || token != _validToken())
        {
            return Error(HttpStatusCode.Unauthorized, "expired_access_token/");
        }

        if (_failures > 0)
        {
            _failures--;
            HttpResponseMessage failure = Status((HttpStatusCode)_failureStatus, "try later");
            if (_failureStatus == 429)
            {
                failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            }

            return failure;
        }

        return endpoint switch
        {
            "files/get_metadata" => GetMetadata(Path(argument)),
            "files/list_folder" => ListFolder(Path(argument), 0),
            "files/list_folder/continue" => Continue(argument!["cursor"]!.GetValue<string>()),
            "files/download" => Download(Path(argument)),
            "files/upload" => contentType == "application/octet-stream"
                ? Upload(argument!, body)
                : Status(HttpStatusCode.BadRequest, "upload needs application/octet-stream"),
            "files/create_folder_v2" => CreateFolder(Path(argument)),
            "files/delete_v2" => Delete(Path(argument)),
            "files/move_v2" => Move(argument!["from_path"]!.GetValue<string>(), argument["to_path"]!.GetValue<string>()),
            "users/get_space_usage" => Json(new JsonObject
            {
                ["used"] = Used,
                ["allocation"] = new JsonObject { [".tag"] = "individual", ["allocated"] = Allocated }
            }),
            _ => Status(HttpStatusCode.NotFound, $"unknown endpoint {endpoint}")
        };
    }

    private HttpResponseMessage GetMetadata(string path)
    {
        if (path.Length == 0)
        {
            return Status(HttpStatusCode.BadRequest, "Error in call to API function \"files/get_metadata\": request body: path: The root folder is unsupported.");
        }

        return _nodes.TryGetValue(path, out Node? node) ? Json(Metadata(node)) : Error(HttpStatusCode.Conflict, "path/not_found/");
    }

    private HttpResponseMessage ListFolder(string path, int offset)
    {
        if (!_nodes.TryGetValue(path, out Node? folder))
        {
            return Error(HttpStatusCode.Conflict, "path/not_found/");
        }

        if (!folder.IsFolder)
        {
            return Error(HttpStatusCode.Conflict, "path/not_folder/");
        }

        Node[] children = [.. _nodes.Values.Where(node => node.Path.Length > 0 && string.Equals(Parent(node.Path), folder.Path, StringComparison.OrdinalIgnoreCase)).OrderBy(static node => node.Path, StringComparer.Ordinal)];
        Node[] page = [.. children.Skip(offset).Take(PageSize)];
        bool more = offset + page.Length < children.Length;
        JsonArray entries = [.. page.Select(node => (JsonNode)Metadata(node))];
        return Json(new JsonObject
        {
            ["entries"] = entries,
            ["cursor"] = $"{offset + page.Length}|{folder.Path}",
            ["has_more"] = more
        });
    }

    private HttpResponseMessage Continue(string cursor)
    {
        int separator = cursor.IndexOf('|', StringComparison.Ordinal);
        return ListFolder(cursor[(separator + 1)..], int.Parse(cursor[..separator], CultureInfo.InvariantCulture));
    }

    private HttpResponseMessage Download(string path)
    {
        if (!_nodes.TryGetValue(path, out Node? node))
        {
            return Error(HttpStatusCode.Conflict, "path/not_found/");
        }

        if (node.IsFolder)
        {
            return Error(HttpStatusCode.Conflict, "path/not_file/");
        }

        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(node.Content!) };
        response.Headers.TryAddWithoutValidation("Dropbox-API-Result", Metadata(node).ToJsonString());
        return response;
    }

    private HttpResponseMessage Upload(JsonObject argument, byte[] content)
    {
        string path = Path(argument);
        _nodes.TryGetValue(path, out Node? existing);
        JsonNode mode = argument["mode"]!;
        string tag = mode is JsonValue ? mode.GetValue<string>() : mode[".tag"]!.GetValue<string>();
        switch (tag)
        {
            case "add" when existing is not null:
                return Error(HttpStatusCode.Conflict, existing.IsFolder ? "path/conflict/folder/" : "path/conflict/file/");
            case "overwrite" when existing is { IsFolder: true }:
                return Error(HttpStatusCode.Conflict, "path/conflict/folder/");
            case "update" when existing is null || existing.IsFolder || existing.Rev != mode["update"]!.GetValue<string>():
                return Error(HttpStatusCode.Conflict, "path/conflict/file/");
        }

        if (existing is null && !_nodes.ContainsKey(Parent(path)))
        {
            // Dropbox creates missing parents.
            EnsureParents(path);
        }

        Node node = new(existing?.Path ?? path, IsFolder: false, content, NextRev(), new DateTimeOffset(2026, 11, 14, 8, 30, 0, TimeSpan.Zero));
        _nodes[path] = node;
        return Json(Metadata(node));
    }

    private HttpResponseMessage CreateFolder(string path)
    {
        if (_nodes.TryGetValue(path, out Node? existing))
        {
            return Error(HttpStatusCode.Conflict, existing.IsFolder ? "path/conflict/folder/" : "path/conflict/file/");
        }

        PutFolder(path);
        return Json(new JsonObject { ["metadata"] = Metadata(_nodes[path]) });
    }

    private HttpResponseMessage Delete(string path)
    {
        if (path.Length == 0 || !_nodes.TryGetValue(path, out Node? node))
        {
            return Error(HttpStatusCode.Conflict, "path_lookup/not_found/");
        }

        foreach (string key in _nodes.Keys.Where(key => key.Equals(path, StringComparison.OrdinalIgnoreCase) || key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _nodes.Remove(key);
        }

        return Json(new JsonObject { ["metadata"] = Metadata(node) });
    }

    private HttpResponseMessage Move(string fromPath, string toPath)
    {
        string from = Normalize(fromPath);
        string to = Normalize(toPath);
        if (!_nodes.TryGetValue(from, out Node? node))
        {
            return Error(HttpStatusCode.Conflict, "from_lookup/not_found/");
        }

        if (_nodes.ContainsKey(to))
        {
            // Includes a change of case alone, which Dropbox does not do in one move.
            return Error(HttpStatusCode.Conflict, "to/conflict/file/");
        }

        EnsureParents(to);
        foreach (KeyValuePair<string, Node> pair in _nodes.Where(pair => pair.Key.Equals(from, StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _nodes.Remove(pair.Key);
            string moved = to + pair.Value.Path[from.Length..];
            _nodes[moved] = pair.Value with { Path = moved };
        }

        return Json(new JsonObject { ["metadata"] = Metadata(_nodes[to]) });
    }

    private void EnsureParents(string path)
    {
        string parent = Parent(path);
        if (parent.Length > 0 && !_nodes.ContainsKey(parent))
        {
            EnsureParents(parent);
            _nodes[parent] = new Node(parent, IsFolder: true, null, null, DateTimeOffset.UnixEpoch);
        }
    }

    private string NextRev() => (++_revision).ToString("x9", CultureInfo.InvariantCulture);

    private static JsonObject Metadata(Node node)
    {
        string name = node.Path[(node.Path.LastIndexOf('/') + 1)..];
        JsonObject metadata = new()
        {
            [".tag"] = node.IsFolder ? "folder" : "file",
            ["name"] = name,
            ["path_lower"] = node.Path.ToLowerInvariant(),
            ["path_display"] = node.Path,
            ["id"] = "id:" + node.Path.ToLowerInvariant()
        };
        if (!node.IsFolder)
        {
            metadata["size"] = node.Content!.LongLength;
            metadata["rev"] = node.Rev;
            metadata["server_modified"] = node.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            metadata["client_modified"] = node.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private static string Path(JsonObject? argument) => Normalize(argument!["path"]!.GetValue<string>());

    private static string Normalize(string path) => path.Length == 0 || path == "/" ? string.Empty : "/" + path.Trim('/');

    private static string Parent(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string summary) =>
        new(status)
        {
            Content = new StringContent(
                new JsonObject { ["error_summary"] = summary, ["error"] = new JsonObject { [".tag"] = summary.Split('/')[0] } }.ToJsonString(),
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage Status(HttpStatusCode status, string text) =>
        new(status) { Content = new StringContent(text) };

    private sealed record Node(string Path, bool IsFolder, byte[]? Content, string? Rev, DateTimeOffset Modified);
}
