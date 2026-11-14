using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.OneDrive;

/// <summary>
/// A fake Microsoft Graph for the OneDrive connector tests (ADR 0267 §6): one drive in memory, with
/// path addressing (<c>root:/a/b:</c>), the app folder (<c>special/approot</c>), case-insensitive
/// names, <c>eTag</c> revisions and <c>If-Match</c>, paged children, pre-authenticated download
/// and upload-session addresses, a quota, and faults a test can inject (no network, an expired
/// token, a status for the next request). No test ever reaches Microsoft.
/// </summary>
internal sealed class FakeGraphServer : HttpMessageHandler
{
    public const string Graph = "https://graph.microsoft.com/v1.0/";
    public const string DownloadHost = "download.fake.test";
    public const string UploadHost = "upload.fake.test";

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UploadSession> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<(HttpStatusCode Status, string? Code, TimeSpan? RetryAfter)> _faults = new();
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly Func<string> _currentToken;
    private int _sequence;

    /// <summary>Initializes the drive.</summary>
    /// <param name="currentToken">The access token the account has now; Graph accepts it unless revoked.</param>
    public FakeGraphServer(Func<string> currentToken)
    {
        _currentToken = currentToken;
        Root = NewNode(null, "root", isFolder: true);
    }

    /// <summary>The drive's root folder.</summary>
    public Node Root { get; }

    /// <summary>Graph stops accepting the account's current access token (HTTP 401).</summary>
    public void RevokeCurrentToken() => _revoked.Add(_currentToken());

    /// <summary>Makes the next <paramref name="count"/> Graph requests answer <paramref name="status"/>.</summary>
    public void FailNext(int count, HttpStatusCode status)
    {
        for (int index = 0; index < count; index++)
        {
            FailNext(status);
        }
    }

    /// <summary>Whether the network works; off, every request throws <see cref="HttpRequestException"/>.</summary>
    public bool Online { get; set; } = true;

    /// <summary>Entries per page of children.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Size of the storage.</summary>
    public long QuotaTotal { get; set; } = 1_000_000_000;

    /// <summary>Whether <c>/me/drive</c> refuses the app-folder access.</summary>
    public bool QuotaForbidden { get; set; }

    /// <summary>Every request received, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Makes the next Graph request answer <paramref name="status"/>.</summary>
    public void FailNext(HttpStatusCode status, string? code = null, TimeSpan? retryAfter = null) => _faults.Enqueue((status, code, retryAfter));

    /// <summary>Puts a file, creating its folders.</summary>
    public Node Put(string content, params string[] path) => Put(Encoding.UTF8.GetBytes(content), path);

    /// <summary>Puts a file, creating its folders.</summary>
    public Node Put(byte[] content, params string[] path)
    {
        Node parent = Folder(path[..^1]);
        Node? existing = parent.Find(path[^1]);
        if (existing is not null)
        {
            existing.Content = content;
            Touch(existing);
            return existing;
        }

        Node file = NewNode(parent, path[^1], isFolder: false);
        file.Content = content;
        return file;
    }

    /// <summary>Gets or creates a folder.</summary>
    public Node Folder(params string[] path)
    {
        Node node = Root;
        foreach (string segment in path)
        {
            node = node.Find(segment) ?? NewNode(node, segment, isFolder: true);
        }

        return node;
    }

    /// <summary>Gets the node at a path, or <see langword="null"/>.</summary>
    public Node? Find(params string[] path)
    {
        Node? node = Root;
        foreach (string segment in path)
        {
            node = node?.Find(segment);
        }

        return node;
    }

    /// <summary>The app folder, created at first access as Microsoft does.</summary>
    public Node AppFolder => Folder("Apps", "Kazo OS");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        RecordedRequest recorded = new(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("If-Match", out IEnumerable<string>? match) ? match.Single() : null,
            request.Content?.Headers.ContentRange?.ToString(),
            body);
        Requests.Add(recorded);
        if (!Online)
        {
            throw new HttpRequestException("offline");
        }

        Uri uri = request.RequestUri!;
        return uri.Host switch
        {
            DownloadHost => Download(recorded),
            UploadHost => Upload(recorded),
            "graph.microsoft.com" => HandleGraph(recorded),
            _ => throw new InvalidOperationException($"Unexpected host {uri.Host}")
        };
    }

    private HttpResponseMessage HandleGraph(RecordedRequest request)
    {
        if (request.Authorization is not { } authorization
            || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || authorization["Bearer ".Length..] != _currentToken()
            || _revoked.Contains(authorization["Bearer ".Length..]))
        {
            return Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken");
        }

        if (_faults.TryDequeue(out var fault))
        {
            HttpResponseMessage failed = Error(fault.Status, fault.Code ?? "fault");
            if (fault.RetryAfter is { } delay)
            {
                failed.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            }

            return failed;
        }

        string path = request.Uri.AbsolutePath["/v1.0/".Length..];
        string query = Uri.UnescapeDataString(request.Uri.Query);
        if (path == "me/drive")
        {
            return QuotaForbidden ? Error(HttpStatusCode.Forbidden, "accessDenied") : Quota();
        }

        Node baseNode;
        string rest;
        if (path.StartsWith("me/drive/items/", StringComparison.Ordinal))
        {
            string[] parts = path["me/drive/items/".Length..].Split('/', 2);
            if (!_nodes.TryGetValue(parts[0], out Node? byId))
            {
                return NotFound();
            }

            baseNode = byId;
            rest = parts.Length > 1 ? "/" + parts[1] : "";
        }
        else if (path.StartsWith("me/drive/special/approot", StringComparison.Ordinal))
        {
            baseNode = AppFolder;
            rest = path["me/drive/special/approot".Length..];
        }
        else if (path.StartsWith("me/drive/root", StringComparison.Ordinal))
        {
            baseNode = Root;
            rest = path["me/drive/root".Length..];
        }
        else
        {
            return Error(HttpStatusCode.BadRequest, "invalidRequest");
        }

        string[] segments = [];
        if (rest.StartsWith(":/", StringComparison.Ordinal))
        {
            int end = rest.IndexOf(':', 2);
            segments = [.. rest[2..end].Split('/').Select(Uri.UnescapeDataString)];
            rest = rest[(end + 1)..];
        }

        Node? parent = baseNode;
        foreach (string segment in segments[..Math.Max(0, segments.Length - 1)])
        {
            parent = parent?.Find(segment);
        }

        Node? item = segments.Length == 0 ? baseNode : parent?.Find(segments[^1]);
        return (request.Method.Method, rest) switch
        {
            ("GET", "") => item is null ? NotFound() : Json(Describe(item)),
            ("GET", "/children") => item is null ? NotFound() : Children(item, query),
            ("POST", "/children") => item is null ? NotFound() : CreateFolder(item, request.Body!),
            ("PUT", "/content") => PutContent(parent, segments, item, request, query),
            ("POST", "/createUploadSession") => CreateSession(parent, segments, item, request),
            ("DELETE", "") => Delete(item),
            ("PATCH", "") => Move(item, request.Body!),
            _ => Error(HttpStatusCode.BadRequest, "invalidRequest")
        };
    }

    /// <summary>Bytes of all the files.</summary>
    public long Used => _nodes.Values.Where(static node => !node.IsFolder).Sum(static node => (long)node.Content.Length);

    private HttpResponseMessage Quota()
    {
        long used = Used;
        return Json(new JsonObject
        {
            ["quota"] = new JsonObject { ["total"] = QuotaTotal, ["remaining"] = QuotaTotal - used, ["used"] = used }
        });
    }

    private HttpResponseMessage Children(Node folder, string query)
    {
        if (!folder.IsFolder)
        {
            return Error(HttpStatusCode.BadRequest, "invalidRequest");
        }

        int skip = query.Contains("$skiptoken=", StringComparison.Ordinal)
            ? int.Parse(query[(query.IndexOf("$skiptoken=", StringComparison.Ordinal) + 11)..], System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        Node[] page = [.. folder.Children.Skip(skip).Take(PageSize)];
        JsonObject answer = new() { ["value"] = new JsonArray([.. page.Select(child => (JsonNode)Describe(child))]) };
        if (skip + PageSize < folder.Children.Count)
        {
            answer["@odata.nextLink"] = $"{Graph}me/drive/items/{folder.Id}/children?$skiptoken={skip + PageSize}";
        }

        return Json(answer);
    }

    private HttpResponseMessage CreateFolder(Node parent, byte[] body)
    {
        JsonNode json = JsonNode.Parse(body)!;
        string name = json["name"]!.GetValue<string>();
        if (parent.Find(name) is not null)
        {
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists");
        }

        return Json(Describe(NewNode(parent, name, isFolder: true)), HttpStatusCode.Created);
    }

    private HttpResponseMessage PutContent(Node? parent, string[] segments, Node? item, RecordedRequest request, string query)
    {
        if (request.Body!.Length > 4 * 1024 * 1024)
        {
            return Error(HttpStatusCode.RequestEntityTooLarge, "requestTooLarge");
        }

        if (parent is null || segments.Length == 0)
        {
            return NotFound();
        }

        if (item is not null && query.Contains("conflictBehavior=fail", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists");
        }

        if (request.IfMatch is { } expected && item?.ETag != expected)
        {
            return Error(HttpStatusCode.PreconditionFailed, "resourceModified");
        }

        Node file = item ?? NewNode(parent, segments[^1], isFolder: false);
        file.Content = request.Body;
        Touch(file);
        return Json(Describe(file), item is null ? HttpStatusCode.Created : HttpStatusCode.OK);
    }

    private HttpResponseMessage CreateSession(Node? parent, string[] segments, Node? item, RecordedRequest request)
    {
        if (parent is null || segments.Length == 0)
        {
            return NotFound();
        }

        if (request.IfMatch is { } expected && item?.ETag != expected)
        {
            return Error(HttpStatusCode.PreconditionFailed, "resourceModified");
        }

        string id = $"session-{++_sequence}";
        _sessions[id] = new UploadSession(parent, segments[^1]);
        return Json(new JsonObject { ["uploadUrl"] = $"https://{UploadHost}/{id}" });
    }

    private HttpResponseMessage Delete(Node? item)
    {
        if (item is null)
        {
            return NotFound();
        }

        if (item.Parent is null)
        {
            return Error(HttpStatusCode.Forbidden, "accessDenied");
        }

        Remove(item);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Move(Node? item, byte[] body)
    {
        if (item is null)
        {
            return NotFound();
        }

        JsonNode json = JsonNode.Parse(body)!;
        Node parent = json["parentReference"]?["id"]?.GetValue<string>() is { } parentId ? _nodes[parentId] : item.Parent!;
        string name = json["name"]?.GetValue<string>() ?? item.Name;
        if (parent.Find(name) is { } other && other != item)
        {
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists");
        }

        item.Parent!.Children.Remove(item);
        item.Parent = parent;
        item.Name = name;
        parent.Children.Add(item);
        Touch(item);
        return Json(Describe(item));
    }

    private HttpResponseMessage Download(RecordedRequest request)
    {
        if (request.Authorization is not null)
        {
            throw new InvalidOperationException("The access token must never go to the download address.");
        }

        return _nodes.TryGetValue(request.Uri.AbsolutePath.Trim('/'), out Node? node)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(node.Content) }
            : NotFound();
    }

    private HttpResponseMessage Upload(RecordedRequest request)
    {
        if (request.Authorization is not null)
        {
            throw new InvalidOperationException("The access token must never go to the upload address.");
        }

        string id = request.Uri.AbsolutePath.Trim('/');
        if (!_sessions.TryGetValue(id, out UploadSession? session))
        {
            return NotFound();
        }

        if (request.Method == HttpMethod.Delete)
        {
            _sessions.Remove(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        System.Net.Http.Headers.ContentRangeHeaderValue range = System.Net.Http.Headers.ContentRangeHeaderValue.Parse(request.ContentRange!);
        if (range.From != session.Received.Length || request.Body!.Length % (320 * 1024) != 0 && range.To + 1 != range.Length)
        {
            return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "invalidRange");
        }

        session.Received.Write(request.Body!);
        if (session.Received.Length < range.Length)
        {
            return Json(new JsonObject { ["nextExpectedRanges"] = new JsonArray($"{session.Received.Length}-") }, HttpStatusCode.Accepted);
        }

        _sessions.Remove(id);
        Node file = session.Parent.Find(session.Name) ?? NewNode(session.Parent, session.Name, isFolder: false);
        file.Content = session.Received.ToArray();
        Touch(file);
        return Json(Describe(file), HttpStatusCode.Created);
    }

    public int OpenSessions => _sessions.Count;

    private JsonObject Describe(Node node)
    {
        JsonObject item = new()
        {
            ["id"] = node.Id,
            ["name"] = node.Name,
            ["eTag"] = node.ETag,
            ["size"] = node.IsFolder ? 0 : node.ReportedSize ?? node.Content.Length,
            ["lastModifiedDateTime"] = node.Modified.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        if (node.IsFolder)
        {
            item["folder"] = new JsonObject { ["childCount"] = node.Children.Count };
        }
        else if (node.IsPackage)
        {
            item["package"] = new JsonObject { ["type"] = "oneNote" };
        }
        else
        {
            item["file"] = new JsonObject { ["mimeType"] = "application/octet-stream" };
            item["@microsoft.graph.downloadUrl"] = $"https://{DownloadHost}/{node.Id}";
        }

        return item;
    }

    private Node NewNode(Node? parent, string name, bool isFolder)
    {
        Node node = new($"item-{++_sequence}", name, isFolder) { Parent = parent };
        Touch(node);
        _nodes[node.Id] = node;
        parent?.Children.Add(node);
        return node;
    }

    private void Touch(Node node)
    {
        node.Version++;
        node.ETag = $"\"{{{node.Id}}},{node.Version}\"";
        node.Modified = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero).AddMinutes(_sequence++);
    }

    private void Remove(Node node)
    {
        foreach (Node child in node.Children.ToArray())
        {
            Remove(child);
        }

        node.Parent?.Children.Remove(node);
        _nodes.Remove(node.Id);
    }

    private static HttpResponseMessage NotFound() => Error(HttpStatusCode.NotFound, "itemNotFound");

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = code } }, status);

    private static HttpResponseMessage Json(JsonNode json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>One request as Graph saw it.</summary>
    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? IfMatch, string? ContentRange, byte[]? Body);

    /// <summary>One item of the fake drive.</summary>
    public sealed class Node(string id, string name, bool isFolder)
    {
        public string Id { get; } = id;

        public string Name { get; set; } = name;

        public bool IsFolder { get; } = isFolder;

        /// <summary>Whether the item is a OneNote package, neither file nor folder.</summary>
        public bool IsPackage { get; set; }

        public Node? Parent { get; set; }

        public List<Node> Children { get; } = [];

        public byte[] Content { get; set; } = [];

        /// <summary>A size Graph reports instead of the content's (a huge file without the memory).</summary>
        public long? ReportedSize { get; set; }

        public int Version { get; set; }

        public string ETag { get; set; } = "";

        public DateTimeOffset Modified { get; set; }

        public string Text => Encoding.UTF8.GetString(Content);

        public Node? Find(string name) =>
            Children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class UploadSession(Node parent, string name)
    {
        public Node Parent { get; } = parent;

        public string Name { get; } = name;

        public MemoryStream Received { get; } = new();
    }
}
