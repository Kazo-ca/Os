using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// A WebDAV server (RFC 4918 class 1) that answers the browser's requests to <see cref="Origin"/>
/// through Playwright's routing (ADR 0282, work item 9): the trimmed OS sends its requests with
/// <c>fetch</c> as it would to a real server, and nothing leaves the test process. It answers like
/// Nextcloud or Apache mod_dav: HTTP Basic, <c>ETag</c>, <c>If-Match</c> and <c>If-None-Match</c>,
/// <c>207 Multi-Status</c>, and the CORS headers a server must send for Kazo OS to reach it.
/// </summary>
internal sealed class RoutedWebDavServer
{
    /// <summary>The origin of the server; never resolved, the routes answer first.</summary>
    public const string Origin = "https://dav.kazoos-e2e.test";

    /// <summary>The user's folder, as Nextcloud names it.</summary>
    public const string BasePath = "/remote.php/dav/files/ben";

    /// <summary>The account's user name.</summary>
    public const string UserName = "ben";

    /// <summary>The account's password.</summary>
    public const string Password = "app-password-1";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private long _etag;

    public RoutedWebDavServer()
    {
        foreach (string folder in new[] { "/remote.php", "/remote.php/dav", "/remote.php/dav/files", BasePath })
        {
            _nodes[folder] = Node.Folder();
        }
    }

    /// <summary>Gets the server address the user types in the form.</summary>
    public static string ServerAddress => Origin + BasePath;

    /// <summary>Gets the methods received, in order (<c>PROPFIND</c>, <c>PUT</c>…), preflights excluded.</summary>
    public List<string> Methods { get; } = [];

    /// <summary>Starts answering the requests of <paramref name="context"/> to <see cref="Origin"/>.</summary>
    public Task RouteAsync(IBrowserContext context) => context.RouteAsync(Origin + "/**", AnswerAsync);

    /// <summary>Places a file in the user's folder, as another device would.</summary>
    public void PlaceFile(string name, string text)
    {
        byte[] content = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            _nodes[BasePath + "/" + name] = new Node(false, content, NextETag());
        }
    }

    /// <summary>Gets the text of a file of the user's folder, or <see langword="null"/> when there is none.</summary>
    public string? ReadFile(string name)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(BasePath + "/" + name, out Node? node) && !node.IsFolder ? Encoding.UTF8.GetString(node.Content) : null;
        }
    }

    private async Task AnswerAsync(IRoute route)
    {
        IRequest request = route.Request;
        Dictionary<string, string> headers = await request.AllHeadersAsync();
        if (request.Method == "OPTIONS")
        {
            // The CORS preflight of a PROPFIND, a PUT or a request with Authorization.
            await FulfillAsync(route, 204, [], [
                new("Access-Control-Allow-Methods", "GET, PUT, DELETE, PROPFIND, MKCOL, MOVE, OPTIONS"),
                new("Access-Control-Allow-Headers", "Authorization, Content-Type, Depth, Destination, Overwrite, If-Match, If-None-Match"),
                new("Access-Control-Max-Age", "600")]);
            return;
        }

        (int status, byte[] body, KeyValuePair<string, string>[] extra) = Handle(request, headers);
        await FulfillAsync(route, status, body, extra);
    }

    private (int Status, byte[] Body, KeyValuePair<string, string>[] Headers) Handle(IRequest request, Dictionary<string, string> headers)
    {
        lock (_gate)
        {
            Methods.Add(request.Method);
            string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password}"));
            if (Header(headers, "authorization") != expected)
            {
                return (401, [], []);
            }

            string path = Decode(new Uri(request.Url).AbsolutePath);
            return request.Method switch
            {
                "PROPFIND" => Propfind(path, Header(headers, "depth") ?? "infinity"),
                "GET" => Get(path),
                "PUT" => Put(path, headers, request.PostDataBuffer ?? []),
                "MKCOL" => Mkcol(path),
                "DELETE" => Delete(path),
                "MOVE" => Move(path, headers),
                _ => (405, [], []),
            };
        }
    }

    private (int, byte[], KeyValuePair<string, string>[]) Propfind(string path, string depth)
    {
        if (!_nodes.TryGetValue(path, out Node? node))
        {
            return (404, [], []);
        }

        StringBuilder xml = new("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\">");
        Describe(xml, path, node);
        if (depth == "1" && node.IsFolder)
        {
            foreach (KeyValuePair<string, Node> child in _nodes.Where(pair => pair.Key != path && Parent(pair.Key) == path))
            {
                Describe(xml, child.Key, child.Value);
            }
        }

        xml.Append("</d:multistatus>");
        return (207, Encoding.UTF8.GetBytes(xml.ToString()), [new("Content-Type", "application/xml; charset=utf-8")]);
    }

    private static void Describe(StringBuilder xml, string path, Node node)
    {
        string href = string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + (node.IsFolder ? "/" : string.Empty);
        xml.Append("<d:response><d:href>").Append(SecurityElement.Escape(href)).Append("</d:href><d:propstat><d:prop>");
        if (node.IsFolder)
        {
            xml.Append("<d:resourcetype><d:collection/></d:resourcetype>");
        }
        else
        {
            xml.Append(CultureInfo.InvariantCulture, $"<d:resourcetype/><d:getcontentlength>{node.Content.Length}</d:getcontentlength>")
                .Append("<d:getlastmodified>Tue, 17 Nov 2026 06:00:00 GMT</d:getlastmodified>")
                .Append("<d:getetag>").Append(SecurityElement.Escape(node.ETag)).Append("</d:getetag>");
        }

        xml.Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
    }

    private (int, byte[], KeyValuePair<string, string>[]) Get(string path) =>
        _nodes.TryGetValue(path, out Node? node) && !node.IsFolder
            ? (200, node.Content, [new("ETag", node.ETag!), new("Content-Type", "application/octet-stream")])
            : (404, [], []);

    private (int, byte[], KeyValuePair<string, string>[]) Put(string path, Dictionary<string, string> headers, byte[] content)
    {
        _nodes.TryGetValue(path, out Node? existing);
        if (existing is { IsFolder: true })
        {
            return (405, [], []);
        }

        if (!ParentIsFolder(path))
        {
            return (409, [], []);
        }

        if ((Header(headers, "if-none-match") == "*" && existing is not null)
            || (Header(headers, "if-match") is { } ifMatch && existing?.ETag != ifMatch))
        {
            return (412, [], []);
        }

        Node written = new(false, content, NextETag());
        _nodes[path] = written;
        return (existing is null ? 201 : 204, [], [new("ETag", written.ETag!)]);
    }

    private (int, byte[], KeyValuePair<string, string>[]) Mkcol(string path)
    {
        if (_nodes.ContainsKey(path))
        {
            return (405, [], []);
        }

        if (!ParentIsFolder(path))
        {
            return (409, [], []);
        }

        _nodes[path] = Node.Folder();
        return (201, [], []);
    }

    private (int, byte[], KeyValuePair<string, string>[]) Delete(string path)
    {
        if (!_nodes.ContainsKey(path))
        {
            return (404, [], []);
        }

        foreach (string key in _nodes.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
        {
            _nodes.Remove(key);
        }

        return (204, [], []);
    }

    private (int, byte[], KeyValuePair<string, string>[]) Move(string path, Dictionary<string, string> headers)
    {
        if (!_nodes.ContainsKey(path) || Header(headers, "destination") is not { } target)
        {
            return (404, [], []);
        }

        string destination = Decode(new Uri(target).AbsolutePath);
        if (_nodes.ContainsKey(destination))
        {
            return (412, [], []);
        }

        if (!ParentIsFolder(destination))
        {
            return (409, [], []);
        }

        foreach (string key in _nodes.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
        {
            _nodes[destination + key[path.Length..]] = _nodes[key];
            _nodes.Remove(key);
        }

        return (201, [], []);
    }

    /// <summary>Answers with the CORS headers a server must send for the browser to hand Kazo OS the response.</summary>
    private static Task FulfillAsync(IRoute route, int status, byte[] body, KeyValuePair<string, string>[] headers)
    {
        Dictionary<string, string> all = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Access-Control-Allow-Origin"] = "*",
            ["Access-Control-Expose-Headers"] = "ETag, Content-Length",
        };
        foreach (KeyValuePair<string, string> header in headers)
        {
            all[header.Key] = header.Value;
        }

        return route.FulfillAsync(new RouteFulfillOptions { Status = status, Headers = all, BodyBytes = body });
    }

    private string NextETag() => $"\"{Interlocked.Increment(ref _etag)}\"";

    private bool ParentIsFolder(string path) => _nodes.TryGetValue(Parent(path), out Node? parent) && parent.IsFolder;

    private static string? Header(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out string? value) ? value : null;

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

    private sealed record Node(bool IsFolder, byte[] Content, string? ETag)
    {
        public static Node Folder() => new(true, [], null);
    }
}
