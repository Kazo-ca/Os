using System.Globalization;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Kazo.Os.Platform.Core.CloudDrives.S3;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.S3;

/// <summary>
/// An S3 compatible service in memory, behind an <see cref="HttpMessageHandler"/>: buckets of keys,
/// <c>ListObjectsV2</c> with prefixes, delimiters and pages, <c>HEAD</c>, <c>GET</c>, <c>PUT</c> with
/// <c>If-Match</c> and <c>If-None-Match</c>, <c>DELETE</c> and <c>CopyObject</c>. Every request's
/// SigV4 signature is checked against the keys it knows, as a real service does, and a test can cut
/// the network (as fetch fails offline or without a CORS rule).
/// </summary>
internal sealed class FakeS3Server : HttpMessageHandler
{
    public const string AccessKeyId = "AKIDEXAMPLE";
    public const string SecretAccessKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    public const string Region = "us-east-1";

    private readonly Dictionary<string, SortedDictionary<string, StoredObject>> _buckets = new(StringComparer.Ordinal);
    private int _etags;

    public FakeS3Server(string host = "s3.example.test")
    {
        Host = host;
    }

    /// <summary>Gets the host of the service; a virtual-host request comes on <c>bucket.Host</c>.</summary>
    public string Host { get; }

    /// <summary>Gets or sets whether the network works; offline, every request fails like fetch.</summary>
    public bool Online { get; set; } = true;

    /// <summary>Gets or sets whether the service honours <c>If-Match</c> and <c>If-None-Match</c> on PUT.</summary>
    public bool ConditionalWrites { get; set; } = true;

    /// <summary>Gets or sets the secret the service expects; another one makes every signature wrong.</summary>
    public string ExpectedSecret { get; set; } = SecretAccessKey;

    /// <summary>Gets or sets the number of keys per listing page the service sends at most.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Gets the requests, as <c>METHOD bucket/key?query-names</c>.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Gets the addresses of the requests.</summary>
    public List<Uri> Addresses { get; } = [];

    /// <summary>Gets the requests as received, headers included (bodies are not kept).</summary>
    public List<HttpRequestMessage> Received { get; } = [];

    /// <summary>Creates an empty bucket.</summary>
    public void CreateBucket(string bucket) => _buckets.TryAdd(bucket, new SortedDictionary<string, StoredObject>(StringComparer.Ordinal));

    /// <summary>Puts an object, as another program of the user would.</summary>
    public void Put(string bucket, string key, string text)
    {
        CreateBucket(bucket);
        _buckets[bucket][key] = Store(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The text of an object, or <see langword="null"/>.</summary>
    public string? Text(string bucket, string key) =>
        _buckets.TryGetValue(bucket, out SortedDictionary<string, StoredObject>? objects) && objects.TryGetValue(key, out StoredObject? found)
            ? Encoding.UTF8.GetString(found.Content)
            : null;

    /// <summary>The keys of a bucket, in order.</summary>
    public IReadOnlyList<string> Keys(string bucket) => [.. _buckets[bucket].Keys];

    /// <summary>The ETag of an object.</summary>
    public string ETagOf(string bucket, string key) => _buckets[bucket][key].ETag;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Online)
        {
            throw new HttpRequestException("TypeError: Failed to fetch");
        }

        Uri uri = request.RequestUri!;
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Addresses.Add(uri);
        Received.Add(request);

        if (Authenticate(request, body) is { } refused)
        {
            return refused;
        }

        (string bucket, string key) = Locate(uri);
        Dictionary<string, string> query = Query(uri);
        Requests.Add($"{request.Method} {bucket}/{key}{(query.Count > 0 ? "?" + string.Join('&', query.Keys.Order(StringComparer.Ordinal)) : string.Empty)}");
        if (!_buckets.TryGetValue(bucket, out SortedDictionary<string, StoredObject>? objects))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchBucket");
        }

        if (request.Method == HttpMethod.Get && key.Length == 0 && query.ContainsKey("list-type"))
        {
            return List(objects, query);
        }

        StoredObject? existing = objects.GetValueOrDefault(key);
        if (request.Method == HttpMethod.Head || request.Method == HttpMethod.Get)
        {
            if (existing is null)
            {
                return request.Method == HttpMethod.Head ? new HttpResponseMessage(HttpStatusCode.NotFound) : Error(HttpStatusCode.NotFound, "NoSuchKey");
            }

            HttpResponseMessage found = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : existing.Content)
            };
            found.Headers.TryAddWithoutValidation("ETag", existing.ETag);
            found.Content.Headers.ContentLength = existing.Content.LongLength;
            found.Content.Headers.LastModified = existing.Modified;
            return found;
        }

        if (request.Method == HttpMethod.Put)
        {
            if (request.Headers.TryGetValues("x-amz-copy-source", out IEnumerable<string>? sources))
            {
                string source = Uri.UnescapeDataString(sources.Single());
                string sourceKey = source[(bucket.Length + 2)..];
                if (!objects.TryGetValue(sourceKey, out StoredObject? copied))
                {
                    return Error(HttpStatusCode.NotFound, "NoSuchKey");
                }

                objects[key] = Store(copied.Content);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<CopyObjectResult><ETag>{SecurityElement.Escape(objects[key].ETag)}</ETag></CopyObjectResult>")
                };
            }

            if (ConditionalWrites && request.Headers.TryGetValues("If-Match", out IEnumerable<string>? match))
            {
                if (existing is null)
                {
                    return Error(HttpStatusCode.NotFound, "NoSuchKey");
                }

                if (match.Single() != existing.ETag)
                {
                    return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
                }
            }

            if (ConditionalWrites && request.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? none) && none.Single() == "*" && existing is not null)
            {
                return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            }

            objects[key] = Store(body);
            HttpResponseMessage written = new(HttpStatusCode.OK);
            written.Headers.TryAddWithoutValidation("ETag", objects[key].ETag);
            return written;
        }

        if (request.Method == HttpMethod.Delete)
        {
            objects.Remove(key);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    private HttpResponseMessage? Authenticate(HttpRequestMessage request, byte[] body)
    {
        string? authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values) ? values.Single() : null;
        if (authorization is null || !authorization.StartsWith(S3SignatureV4.Algorithm + " ", StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.Forbidden, "AccessDenied");
        }

        Dictionary<string, string> parts = authorization[(S3SignatureV4.Algorithm.Length + 1)..]
            .Split(", ")
            .Select(static part => part.Split('=', 2))
            .ToDictionary(static pair => pair[0], static pair => pair[1], StringComparer.Ordinal);
        string[] credential = parts["Credential"].Split('/');
        if (credential[0] != AccessKeyId)
        {
            return Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId");
        }

        if (credential[2] != Region || credential[3] != "s3" || credential[4] != "aws4_request")
        {
            return Error(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed");
        }

        string contentHash = request.Headers.GetValues("x-amz-content-sha256").Single();
        if (contentHash != S3SignatureV4.PayloadHash(body))
        {
            return Error(HttpStatusCode.BadRequest, "XAmzContentSHA256Mismatch");
        }

        DateTimeOffset timestamp = DateTimeOffset.ParseExact(
            request.Headers.GetValues("x-amz-date").Single(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Dictionary<string, string> signed = new(StringComparer.Ordinal);
        foreach (string name in parts["SignedHeaders"].Split(';'))
        {
            // The browser sends Host from the address; nothing else may stand in for it.
            signed[name] = name == "host" ? request.RequestUri!.Authority : string.Join(',', request.Headers.GetValues(name));
        }

        string expected = S3SignatureV4.Authorize(
            request.Method.Method, request.RequestUri!, signed, contentHash, Region, AccessKeyId, ExpectedSecret, timestamp);
        return expected == authorization ? null : Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
    }

    private (string Bucket, string Key) Locate(Uri uri)
    {
        string path = uri.AbsolutePath.TrimStart('/');
        if (uri.Host != Host && uri.Host.EndsWith("." + Host, StringComparison.Ordinal))
        {
            return (uri.Host[..^(Host.Length + 1)], Uri.UnescapeDataString(path));
        }

        int slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash < 0
            ? (Uri.UnescapeDataString(path), string.Empty)
            : (Uri.UnescapeDataString(path[..slash]), Uri.UnescapeDataString(path[(slash + 1)..]));
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(static part => part.Split('=', 2))
        .ToDictionary(
            static pair => Uri.UnescapeDataString(pair[0]),
            static pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty,
            StringComparer.Ordinal);

    private HttpResponseMessage List(SortedDictionary<string, StoredObject> objects, Dictionary<string, string> query)
    {
        string prefix = query.GetValueOrDefault("prefix") ?? string.Empty;
        string? delimiter = query.GetValueOrDefault("delimiter") is { Length: > 0 } d ? d : null;
        int max = Math.Min(PageSize, query.TryGetValue("max-keys", out string? m) ? int.Parse(m, CultureInfo.InvariantCulture) : 1000);
        string? after = query.TryGetValue("continuation-token", out string? token) ? Encoding.UTF8.GetString(Convert.FromBase64String(token)) : null;

        StringBuilder xml = new("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        List<string> contents = [];
        SortedSet<string> prefixes = new(StringComparer.Ordinal);
        string? last = null;
        bool truncated = false;
        foreach ((string key, StoredObject stored) in objects)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || (after is not null && string.CompareOrdinal(key, after) <= 0))
            {
                continue;
            }

            string? common = null;
            if (delimiter is not null)
            {
                int index = key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
                if (index >= 0)
                {
                    common = key[..(index + delimiter.Length)];
                }
            }

            if (common is not null && prefixes.Contains(common))
            {
                last = key;
                continue;
            }

            if (contents.Count + prefixes.Count >= max)
            {
                truncated = true;
                break;
            }

            if (common is not null)
            {
                prefixes.Add(common);
            }
            else
            {
                contents.Add(
                    $"<Contents><Key>{SecurityElement.Escape(key)}</Key><LastModified>{stored.Modified.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}</LastModified>"
                    + $"<ETag>{SecurityElement.Escape(stored.ETag)}</ETag><Size>{stored.Content.Length}</Size><StorageClass>STANDARD</StorageClass></Contents>");
            }

            last = key;
        }

        xml.Append("<IsTruncated>").Append(truncated ? "true" : "false").Append("</IsTruncated>");
        foreach (string item in contents)
        {
            xml.Append(item);
        }

        foreach (string common in prefixes)
        {
            xml.Append("<CommonPrefixes><Prefix>").Append(SecurityElement.Escape(common)).Append("</Prefix></CommonPrefixes>");
        }

        if (truncated && last is not null)
        {
            xml.Append("<NextContinuationToken>").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(last))).Append("</NextContinuationToken>");
        }

        xml.Append("</ListBucketResult>");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }

    private StoredObject Store(byte[] content) =>
        new([.. content], $"\"{Convert.ToHexStringLower(SHA256.HashData(content))[..24]}{++_etags:x8}\"", new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero).AddMinutes(_etags));

    private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>", Encoding.UTF8, "application/xml")
    };

    private sealed record StoredObject(byte[] Content, string ETag, DateTimeOffset Modified);
}
