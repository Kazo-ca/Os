using System.Globalization;
using System.Net.Http;
using System.Security;
using System.Text;
using Kazo.Os.Samples.WebDavDrive;

namespace Kazo.Os.Samples.WebDavDrive.Tests;

/// <summary>
/// A WebDAV server in the test process (RFC 4918 class 1, RFC 4331 quotas), answering the
/// sample's requests as Nextcloud or Apache mod_dav would: HTTP Basic, <c>ETag</c>,
/// <c>If-Match</c>/<c>If-None-Match</c>, <c>207 Multi-Status</c> with escaped <c>href</c>s. No
/// socket and no real service: the tests never leave the process.
/// </summary>
internal sealed class LocalWebDavServer : IWebDavTransport
{
    public const string Origin = "https://dav.test.example";
    public const string BasePath = "/remote.php/dav/files/ben";
    public const string UserName = "ben";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { [BasePath] = Node.Folder() };
    private long _etag;

    public LocalWebDavServer()
    {
        // The parents of the user's folder exist, as on a real server.
        _nodes["/remote.php"] = Node.Folder();
        _nodes["/remote.php/dav"] = Node.Folder();
        _nodes["/remote.php/dav/files"] = Node.Folder();
    }

    public string Password { get; set; } = "app-password-1";

    public bool Online { get; set; } = true;

    public long? QuotaBytes { get; set; } = 50_000_000;

    public int Requests { get; private set; }

    public List<string> Methods { get; } = [];

    public void PlaceFile(string path, long length)
    {
        lock (_gate)
        {
            _nodes[path] = new Node(false, [], length, $"\"{++_etag}\"");
        }
    }

    public Task<WebDavResponse> SendAsync(WebDavRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Requests++;
            Methods.Add(request.Method.Method);
            if (!Online)
            {
                throw new HttpRequestException("The network is down.");
            }

            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal(new Uri(Origin).Authority, request.Uri.Authority);
            if (Header(request, "Authorization") != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password}")))
            {
                return Answer(401);
            }

            string path = Decode(request.Uri.AbsolutePath);
            return request.Method.Method switch
            {
                "PROPFIND" => Propfind(path, Header(request, "Depth") ?? "infinity"),
                "GET" => Get(path),
                "PUT" => Put(path, request),
                "MKCOL" => Mkcol(path),
                "DELETE" => Delete(path),
                "MOVE" => MoveTo(path, request),
                _ => Answer(405),
            };
        }
    }

    private Task<WebDavResponse> Propfind(string path, string depth)
    {
        if (!_nodes.TryGetValue(path, out Node? node))
        {
            return Answer(404);
        }

        StringBuilder xml = new("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\">");
        Describe(xml, path, node);
        if (depth == "1" && node.IsFolder)
        {
            foreach (KeyValuePair<string, Node> child in _nodes.Where(pair => Parent(pair.Key) == path && pair.Key != path))
            {
                Describe(xml, child.Key, child.Value);
            }
        }

        xml.Append("</d:multistatus>");
        return Answer(207, Encoding.UTF8.GetBytes(xml.ToString()));
    }

    private void Describe(StringBuilder xml, string path, Node node)
    {
        string href = string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + (node.IsFolder ? "/" : string.Empty);
        xml.Append("<d:response><d:href>").Append(SecurityElement.Escape(href)).Append("</d:href><d:propstat><d:prop>");
        if (node.IsFolder)
        {
            xml.Append("<d:resourcetype><d:collection/></d:resourcetype>");
            if (path == BasePath && QuotaBytes is { } quota)
            {
                long used = _nodes.Values.Sum(static n => n.Length);
                xml.Append(CultureInfo.InvariantCulture, $"<d:quota-available-bytes>{Math.Max(0, quota - used)}</d:quota-available-bytes><d:quota-used-bytes>{used}</d:quota-used-bytes>");
            }
        }
        else
        {
            xml.Append(CultureInfo.InvariantCulture, $"<d:resourcetype/><d:getcontentlength>{node.Length}</d:getcontentlength>")
                .Append("<d:getlastmodified>Tue, 17 Nov 2026 06:00:00 GMT</d:getlastmodified>")
                .Append("<d:getetag>").Append(SecurityElement.Escape(node.ETag)).Append("</d:getetag>");
        }

        xml.Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
    }

    private Task<WebDavResponse> Get(string path) =>
        _nodes.TryGetValue(path, out Node? node) && !node.IsFolder
            ? Answer(200, node.Content, [new("Content-Length", node.Length.ToString(CultureInfo.InvariantCulture)), new("ETag", node.ETag!)])
            : Answer(404);

    private Task<WebDavResponse> Put(string path, WebDavRequest request)
    {
        _nodes.TryGetValue(path, out Node? existing);
        if (existing is { IsFolder: true })
        {
            return Answer(405);
        }

        if (!ParentIsFolder(path))
        {
            return Answer(409);
        }

        if (Header(request, "If-None-Match") == "*" && existing is not null)
        {
            return Answer(412);
        }

        if (Header(request, "If-Match") is { } ifMatch && existing?.ETag != ifMatch)
        {
            return Answer(412);
        }

        byte[] content = request.Body?.ToArray() ?? [];
        long used = _nodes.Where(pair => pair.Key != path).Sum(static pair => pair.Value.Length);
        if (QuotaBytes is { } quota && used + content.Length > quota)
        {
            return Answer(507);
        }

        _nodes[path] = new Node(false, content, content.Length, $"\"{++_etag}\"");
        return Answer(existing is null ? 201 : 204);
    }

    private Task<WebDavResponse> Mkcol(string path)
    {
        if (_nodes.ContainsKey(path))
        {
            return Answer(405);
        }

        if (!ParentIsFolder(path))
        {
            return Answer(409);
        }

        _nodes[path] = Node.Folder();
        return Answer(201);
    }

    private Task<WebDavResponse> Delete(string path)
    {
        if (!_nodes.ContainsKey(path))
        {
            return Answer(404);
        }

        foreach (string key in _nodes.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
        {
            _nodes.Remove(key);
        }

        return Answer(204);
    }

    private Task<WebDavResponse> MoveTo(string path, WebDavRequest request)
    {
        if (!_nodes.ContainsKey(path))
        {
            return Answer(404);
        }

        string destination = Decode(new Uri(Header(request, "Destination")!).AbsolutePath);
        if (_nodes.ContainsKey(destination))
        {
            return Header(request, "Overwrite") == "F" ? Answer(412) : Answer(501);
        }

        if (!ParentIsFolder(destination))
        {
            return Answer(409);
        }

        foreach (string key in _nodes.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
        {
            _nodes[destination + key[path.Length..]] = _nodes[key];
            _nodes.Remove(key);
        }

        return Answer(201);
    }

    private bool ParentIsFolder(string path) => _nodes.TryGetValue(Parent(path), out Node? parent) && parent.IsFolder;

    private static string Decode(string absolutePath)
    {
        string decoded = string.Join('/', absolutePath.Split('/').Select(Uri.UnescapeDataString));
        return decoded.Length > 1 ? decoded.TrimEnd('/') : decoded;
    }

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string? Header(WebDavRequest request, string name) =>
        request.Headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static Task<WebDavResponse> Answer(int status, byte[]? body = null, KeyValuePair<string, string>[]? headers = null) =>
        Task.FromResult(new WebDavResponse(status, headers ?? [], new MemoryStream(body ?? [])));

    private sealed record Node(bool IsFolder, byte[] Content, long Length, string? ETag)
    {
        public static Node Folder() => new(true, [], 0, null);
    }
}
