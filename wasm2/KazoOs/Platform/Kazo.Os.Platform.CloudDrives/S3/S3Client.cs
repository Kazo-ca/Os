using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.S3;

/// <summary>One object of a listing or of a <c>HEAD</c>.</summary>
/// <param name="Key">Full key of the object.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ETag">The service's <c>ETag</c>, quotes included, or <see langword="null"/> when the service (or its CORS rule) hides it.</param>
/// <param name="LastModifiedUtc">Last modification, when the service gives it.</param>
internal sealed record S3Object(string Key, long Size, string? ETag, DateTimeOffset? LastModifiedUtc);

/// <summary>One page of <c>ListObjectsV2</c>.</summary>
/// <param name="Objects">The objects (<c>Contents</c>).</param>
/// <param name="Prefixes">The common prefixes, the "folders" under a delimiter.</param>
/// <param name="NextContinuationToken">Token of the next page; <see langword="null"/> on the last one.</param>
internal sealed record S3ListPage(IReadOnlyList<S3Object> Objects, IReadOnlyList<string> Prefixes, string? NextContinuationToken);

/// <summary>
/// The S3 REST calls a drive needs (ADR 0267 §6), signed with SigV4, over <see cref="HttpClient"/>
/// (the browser's <c>fetch</c> on WebAssembly, so the bucket must have a CORS rule for the OS's
/// origin). Every failure is a <see cref="HostFolderException"/> whose kind the drive understands;
/// the S3 error code, when there is one, is in an inner <see cref="S3ServiceException"/>.
/// </summary>
internal sealed class S3Client
{
    /// <summary>The largest object read or written at once (ADR 0245, ADR 0267 §10).</summary>
    public const long MaxObjectBytes = 128L * 1024 * 1024;

    private static readonly HttpRequestOptionsKey<IDictionary<string, object>> FetchOptions = new("WebAssemblyFetchOptions");

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    /// <summary>Initializes the client.</summary>
    /// <param name="http">The HTTP client (a fake handler in tests).</param>
    /// <param name="timeProvider">The real clock: the service refuses a signature more than 15 minutes off.</param>
    public S3Client(HttpClient http, TimeProvider? timeProvider = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Lists one page of objects under <paramref name="prefix"/>.</summary>
    public async ValueTask<S3ListPage> ListAsync(
        S3Connection connection,
        string bucket,
        string prefix,
        string? delimiter,
        int? maxKeys,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        List<(string Name, string Value)> query = [("list-type", "2"), ("prefix", prefix)];
        if (delimiter is not null)
        {
            query.Add(("delimiter", delimiter));
        }

        if (maxKeys is { } max)
        {
            query.Add(("max-keys", max.ToString(CultureInfo.InvariantCulture)));
        }

        if (continuationToken is not null)
        {
            query.Add(("continuation-token", continuationToken));
        }

        using HttpResponseMessage response = await SendAsync(
            connection, HttpMethod.Get, bucket, key: null, query, body: null, extraHeaders: null, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        List<S3Object> objects = [];
        foreach (string contents in S3Xml.Elements(xml, "Contents"))
        {
            if (S3Xml.Value(contents, "Key") is not { Length: > 0 } key)
            {
                continue;
            }

            long size = long.TryParse(S3Xml.Value(contents, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
            DateTimeOffset? modified = DateTimeOffset.TryParse(
                S3Xml.Value(contents, "LastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date)
                ? date
                : null;
            objects.Add(new S3Object(key, size, S3Xml.Value(contents, "ETag"), modified));
        }

        List<string> prefixes = [];
        foreach (string common in S3Xml.Elements(xml, "CommonPrefixes"))
        {
            if (S3Xml.Value(common, "Prefix") is { Length: > 0 } value)
            {
                prefixes.Add(value);
            }
        }

        string? next = string.Equals(S3Xml.Value(xml, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase)
            ? S3Xml.Value(xml, "NextContinuationToken")
            : null;
        return new S3ListPage(objects, prefixes, next is { Length: > 0 } ? next : null);
    }

    /// <summary>Describes an object; <see langword="null"/> when there is none.</summary>
    public async ValueTask<S3Object?> HeadAsync(S3Connection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await SendAsync(
                connection, HttpMethod.Head, bucket, key, query: [], body: null, extraHeaders: null, cancellationToken).ConfigureAwait(false);
            return Describe(key, response);
        }
        catch (HostFolderException exception) when (exception.Kind == HostFolderErrorKind.NotFound)
        {
            return null;
        }
    }

    /// <summary>Reads a whole object.</summary>
    /// <exception cref="HostFolderException"><see cref="HostFolderErrorKind.FileTooLarge"/> past <see cref="MaxObjectBytes"/>.</exception>
    public async ValueTask<(byte[] Content, S3Object Object)> GetAsync(S3Connection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            connection, HttpMethod.Get, bucket, key, query: [], body: null, extraHeaders: null, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaxObjectBytes)
        {
            throw TooLarge(key);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxObjectBytes)
            {
                throw TooLarge(key);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), Describe(key, response));
    }

    /// <summary>
    /// Writes a whole object. <paramref name="ifMatch"/> sends <c>If-Match</c> (the write is refused
    /// with <see cref="HostFolderErrorKind.RevisionConflict"/> when the object changed);
    /// <paramref name="ifNoneMatch"/> sends <c>If-None-Match: *</c> (refused with
    /// <see cref="HostFolderErrorKind.AlreadyExists"/> when the object exists).
    /// </summary>
    public async ValueTask PutAsync(
        S3Connection connection,
        string bucket,
        string key,
        byte[] content,
        string? ifMatch,
        bool ifNoneMatch,
        CancellationToken cancellationToken)
    {
        if (content.LongLength > MaxObjectBytes)
        {
            throw TooLarge(key);
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        if (ifMatch is not null)
        {
            headers["If-Match"] = ifMatch;
        }

        if (ifNoneMatch)
        {
            headers["If-None-Match"] = "*";
        }

        try
        {
            using HttpResponseMessage response = await SendAsync(
                connection, HttpMethod.Put, bucket, key, query: [], content, headers, cancellationToken).ConfigureAwait(false);
        }
        catch (HostFolderException exception) when (ifNoneMatch && exception.Kind == HostFolderErrorKind.RevisionConflict)
        {
            throw new HostFolderException(HostFolderErrorKind.AlreadyExists, $"{key} already exists", exception);
        }
    }

    /// <summary>Deletes an object; S3 answers the same whether it existed or not.</summary>
    public async ValueTask DeleteAsync(S3Connection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            connection, HttpMethod.Delete, bucket, key, query: [], body: null, extraHeaders: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies an object inside the bucket, on the service (<c>CopyObject</c>).</summary>
    public async ValueTask CopyAsync(S3Connection connection, string bucket, string sourceKey, string destinationKey, CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["x-amz-copy-source"] = $"/{bucket}/{S3SignatureV4.Encode(sourceKey, keepSlash: true)}"
        };
        using HttpResponseMessage response = await SendAsync(
            connection, HttpMethod.Put, bucket, destinationKey, query: [], body: null, headers, cancellationToken).ConfigureAwait(false);

        // CopyObject may answer 200 and still fail: the error is then in the body.
        string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (xml.Contains("<Error>", StringComparison.Ordinal))
        {
            string code = S3Xml.Value(xml, "Code") ?? "InternalError";
            throw new HostFolderException(HostFolderErrorKind.Unavailable, $"s3: the copy of {sourceKey} failed ({code})", new S3ServiceException(500, code));
        }
    }

    /// <summary>Builds the address of a request: path or virtual-host addressing, key and query encoded for SigV4.</summary>
    internal static Uri BuildUri(S3Connection connection, string bucket, string? key, IReadOnlyList<(string Name, string Value)> query)
    {
        Uri endpoint = connection.Endpoint;
        StringBuilder address = new();
        address.Append(endpoint.Scheme).Append("://");
        if (connection.Addressing == S3AddressingStyle.VirtualHost)
        {
            address.Append(bucket).Append('.');
        }

        address.Append(endpoint.Authority).Append('/');
        if (connection.Addressing == S3AddressingStyle.Path)
        {
            address.Append(S3SignatureV4.Encode(bucket)).Append('/');
        }

        if (key is not null)
        {
            address.Append(S3SignatureV4.Encode(key, keepSlash: true));
        }

        if (query.Count > 0)
        {
            address.Append('?').Append(string.Join('&', query.Select(static pair =>
                $"{S3SignatureV4.Encode(pair.Name)}={S3SignatureV4.Encode(pair.Value)}")));
        }

        return new Uri(address.ToString(), UriKind.Absolute);
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        S3Connection connection,
        HttpMethod method,
        string bucket,
        string? key,
        IReadOnlyList<(string Name, string Value)> query,
        byte[]? body,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildUri(connection, bucket, key, query);
        DateTimeOffset now = _time.GetUtcNow();
        string payloadHash = S3SignatureV4.PayloadHash(body);
        Dictionary<string, string> signed = new(StringComparer.Ordinal)
        {
            ["host"] = uri.Authority,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = S3SignatureV4.AmzDate(now)
        };
        if (extraHeaders is not null)
        {
            foreach ((string name, string value) in extraHeaders)
            {
                if (name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
                {
                    signed[name.ToLowerInvariant()] = value;
                }
            }
        }

        HttpRequestMessage request = new(method, uri);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            S3SignatureV4.Authorize(method.Method, uri, signed, payloadHash, connection.Region, connection.AccessKeyId, connection.SecretAccessKey, now));
        foreach ((string name, string value) in signed)
        {
            // The browser sets Host itself and refuses a page that tries to.
            if (name != "host")
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (extraHeaders is not null)
        {
            foreach ((string name, string value) in extraHeaders)
            {
                if (!name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        else if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent([]);
        }

        // The browser's HTTP cache would keep a listing or an object S3 does not mark as private:
        // the drive must always see the service as it is.
        request.Options.Set(FetchOptions, new Dictionary<string, object>(StringComparer.Ordinal) { ["cache"] = "no-store" });

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // fetch fails the same way offline and when the bucket has no CORS rule for this origin.
            throw new HostFolderException(HostFolderErrorKind.Unavailable, $"s3: {connection.Endpoint.Host} cannot be reached", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HostFolderException(HostFolderErrorKind.Unavailable, $"s3: {connection.Endpoint.Host} did not answer in time", exception);
        }
        finally
        {
            request.Dispose();
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string text = method == HttpMethod.Head
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw Failure((int)response.StatusCode, S3Xml.Value(text, "Code"), key ?? bucket);
        }
    }

    /// <summary>Maps an S3 error answer to the failure kind a drive understands.</summary>
    internal static HostFolderException Failure(int status, string? code, string what)
    {
        S3ServiceException inner = new(status, code);
        HostFolderErrorKind kind = (status, code) switch
        {
            (404, _) => HostFolderErrorKind.NotFound,
            (412, _) => HostFolderErrorKind.RevisionConflict,
            (409, "ConditionalRequestConflict") => HostFolderErrorKind.RevisionConflict,
            (403, _) => HostFolderErrorKind.NotAllowed,
            (401, _) => HostFolderErrorKind.NotAllowed,
            (400, "EntityTooLarge") => HostFolderErrorKind.FileTooLarge,
            (400 or 403, "QuotaExceeded") => HostFolderErrorKind.NoSpace,
            (507, _) => HostFolderErrorKind.NoSpace,
            ( >= 500, _) => HostFolderErrorKind.Unavailable,
            (408 or 429, _) => HostFolderErrorKind.Unavailable,
            _ => HostFolderErrorKind.Failure
        };
        return new HostFolderException(kind, $"s3: {what}: {status} {code}".TrimEnd(), inner);
    }

    private static S3Object Describe(string key, HttpResponseMessage response) => new(
        key,
        response.Content.Headers.ContentLength ?? 0,
        response.Headers.ETag?.ToString() ?? (response.Headers.TryGetValues("ETag", out IEnumerable<string>? values) ? values.FirstOrDefault() : null),
        response.Content.Headers.LastModified);

    private static HostFolderException TooLarge(string key) =>
        new(HostFolderErrorKind.FileTooLarge, $"s3: {key} is larger than {MaxObjectBytes / (1024 * 1024)} MiB");
}

/// <summary>An error answer of the S3 service, kept as the inner exception of a <see cref="HostFolderException"/>.</summary>
/// <param name="status">HTTP status.</param>
/// <param name="code">S3 error code (<c>NoSuchBucket</c>, <c>SignatureDoesNotMatch</c>…), when the body had one.</param>
internal sealed class S3ServiceException(int status, string? code) : Exception($"S3 answered {status} {code}")
{
    /// <summary>Gets the HTTP status.</summary>
    public int Status { get; } = status;

    /// <summary>Gets the S3 error code.</summary>
    public string? Code { get; } = code;

    /// <summary>Gets whether the service refused the keys themselves (unknown key, wrong secret), not one object.</summary>
    public bool KeysRefused => Code is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" or "ExpiredToken" or "AccountProblem";
}

/// <summary>
/// The few XML answers of S3 read without an XML library (which would weigh on the WebAssembly
/// download): elements are found by name, not nested in themselves, and entities are decoded.
/// </summary>
internal static class S3Xml
{
    /// <summary>The inner text of every <c>&lt;name&gt;</c> element, in order.</summary>
    public static IEnumerable<string> Elements(string xml, string name)
    {
        string open = $"<{name}>";
        string close = $"</{name}>";
        int index = 0;
        while (index < xml.Length)
        {
            int start = xml.IndexOf(open, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            int content = start + open.Length;
            int end = xml.IndexOf(close, content, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            yield return xml[content..end];
            index = end + close.Length;
        }
    }

    /// <summary>The decoded text of the first <c>&lt;name&gt;</c> element; <see langword="null"/> when there is none.</summary>
    public static string? Value(string xml, string name) =>
        Elements(xml, name).Select(Decode).FirstOrDefault();

    /// <summary>Decodes the five XML entities and character references.</summary>
    public static string Decode(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        StringBuilder builder = new(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            int semicolon = c == '&' ? text.IndexOf(';', index) : -1;
            if (semicolon < 0)
            {
                builder.Append(c);
                index++;
                continue;
            }

            string entity = text[(index + 1)..semicolon];
            string? decoded = entity switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                _ when entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(entity[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) && hex is > 0 and <= 0x10FFFF
                    => char.ConvertFromUtf32(hex),
                _ when entity.StartsWith('#')
                    && int.TryParse(entity[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec) && dec is > 0 and <= 0x10FFFF
                    => char.ConvertFromUtf32(dec),
                _ => null
            };
            if (decoded is null)
            {
                builder.Append(c);
                index++;
            }
            else
            {
                builder.Append(decoded);
                index = semicolon + 1;
            }
        }

        return builder.ToString();
    }
}
