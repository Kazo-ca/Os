using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// One direct web request (ADR 0282 §4), sent by <see cref="KazoDirectHttpClient"/> through the
/// browser's <c>fetch</c>, without the simulated network and without the Kazo server.
/// </summary>
/// <remarks>
/// The browser decides some headers itself: <c>Host</c>, <c>Cookie</c>, <c>Origin</c>,
/// <c>Referer</c>, <c>Content-Length</c> and the other headers a page may not set are dropped or
/// refused by <c>fetch</c>. <c>Content-Type</c> comes from <see cref="Content"/>.
/// </remarks>
public sealed class KazoDirectHttpRequest
{
    /// <summary>Initializes a request.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="requestUri">Absolute <c>https</c> URI, or a relative one when the client has a <see cref="KazoDirectHttpClient.BaseAddress"/>.</param>
    public KazoDirectHttpRequest(HttpMethod method, Uri requestUri)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        RequestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
    }

    /// <summary>Gets the HTTP method.</summary>
    public HttpMethod Method { get; }

    /// <summary>Gets the request URI.</summary>
    public Uri RequestUri { get; }

    /// <summary>Gets the request headers, sent in order (<c>Authorization</c>, <c>Accept</c>, a service's own headers…).</summary>
    public IList<KeyValuePair<string, string>> Headers { get; } = [];

    /// <summary>Gets or sets the body, or <see langword="null"/> for none.</summary>
    public KazoDirectHttpContent? Content { get; set; }
}

/// <summary>
/// The body of a <see cref="KazoDirectHttpRequest"/>: bytes, text, JSON or a stream.
/// </summary>
/// <remarks>
/// A stream body is read when the request is sent; the browser sends it once it has it whole
/// (<c>fetch</c> streams an upload only over HTTP/2 and not in every browser), so an app keeps an
/// upload within the service's own chunk size.
/// </remarks>
public sealed class KazoDirectHttpContent
{
    private KazoDirectHttpContent(ReadOnlyMemory<byte>? bytes, Stream? stream, string contentType, long? length)
    {
        Bytes = bytes;
        Stream = stream;
        ContentType = contentType;
        Length = length;
    }

    /// <summary>Gets the <c>Content-Type</c> sent with the body.</summary>
    public string ContentType { get; }

    /// <summary>Gets the body's length in bytes, or <see langword="null"/> when a stream does not tell it.</summary>
    public long? Length { get; }

    /// <summary>Gets the bytes of a byte, text or JSON body; <see langword="null"/> for a stream body.</summary>
    internal ReadOnlyMemory<byte>? Bytes { get; }

    /// <summary>Gets the stream of a stream body; <see langword="null"/> otherwise.</summary>
    internal Stream? Stream { get; }

    /// <summary>Creates a body from bytes.</summary>
    /// <param name="content">The bytes, kept by reference: do not change them before the request is sent.</param>
    /// <param name="contentType">The <c>Content-Type</c>; <c>application/octet-stream</c> by default.</param>
    public static KazoDirectHttpContent FromBytes(ReadOnlyMemory<byte> content, string contentType = SimulatedMediaTypes.OctetStream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return new(content, null, contentType, content.Length);
    }

    /// <summary>Creates a UTF-8 text body.</summary>
    /// <param name="content">The text.</param>
    /// <param name="mediaType">The media type, <c>text/plain</c> by default; <c>; charset=utf-8</c> is added.</param>
    public static KazoDirectHttpContent FromString(string content, string mediaType = "text/plain")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new(bytes, null, $"{mediaType}; charset=utf-8", bytes.Length);
    }

    /// <summary>Creates a JSON body with a source-generated serializer (trim-safe).</summary>
    /// <param name="value">The value.</param>
    /// <param name="jsonTypeInfo">Its type information.</param>
    public static KazoDirectHttpContent FromJson<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo);
        return new(bytes, null, "application/json; charset=utf-8", bytes.Length);
    }

    /// <summary>Creates a body read from a stream when the request is sent.</summary>
    /// <param name="content">A readable stream; the request reads it from its current position and does not dispose it.</param>
    /// <param name="contentType">The <c>Content-Type</c>.</param>
    /// <param name="length">The length in bytes, or <see langword="null"/> when unknown.</param>
    public static KazoDirectHttpContent FromStream(Stream content, string contentType = SimulatedMediaTypes.OctetStream, long? length = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!content.CanRead)
        {
            throw new ArgumentException("The stream cannot be read.", nameof(content));
        }

        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A length cannot be negative.");
        }

        return new(null, content, contentType, length);
    }

    /// <summary>Creates a body from a <see cref="KazoHttpContent"/> built for <see cref="KazoHttpClient"/>.</summary>
    /// <param name="content">The body.</param>
    public static KazoDirectHttpContent From(KazoHttpContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new(content.Bytes, null, content.ContentType, content.Length);
    }
}

/// <summary>
/// The response to a <see cref="KazoDirectHttpRequest"/>, returned as soon as its headers arrive;
/// the body is read from <see cref="Content"/> as it comes. Dispose it to release the connection.
/// </summary>
/// <remarks>
/// A cross-origin <c>fetch</c> shows only the headers the service exposes to pages
/// (<c>Access-Control-Expose-Headers</c>) plus the CORS-safelisted ones (<c>Content-Type</c>,
/// <c>Content-Length</c>, <c>Cache-Control</c>, <c>Expires</c>, <c>Last-Modified</c>,
/// <c>Content-Language</c>, <c>Pragma</c>).
/// </remarks>
public sealed class KazoDirectHttpResponse : IDisposable, IAsyncDisposable
{
    private readonly IDisposable? _owner;
    private bool _disposed;

    /// <summary>Initializes a response (used by network gateways and tests).</summary>
    /// <param name="statusCode">Status code, e.g. 200.</param>
    /// <param name="reasonPhrase">Reason phrase; may be empty (HTTP/2 has none).</param>
    /// <param name="requestUri">Final URI, after the redirects the browser followed.</param>
    /// <param name="headers">Response and content headers visible to the page.</param>
    /// <param name="content">The body stream; the response disposes it.</param>
    public KazoDirectHttpResponse(
        int statusCode, string reasonPhrase, Uri requestUri, IReadOnlyList<KeyValuePair<string, string>> headers, Stream content)
        : this(statusCode, reasonPhrase, requestUri, headers, content, owner: null)
    {
    }

    /// <summary>Initializes a response that also disposes <paramref name="owner"/> (the platform's own message).</summary>
    internal KazoDirectHttpResponse(
        int statusCode, string reasonPhrase, Uri requestUri, IReadOnlyList<KeyValuePair<string, string>> headers, Stream content, IDisposable? owner)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase ?? string.Empty;
        RequestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        _owner = owner;
    }

    /// <summary>Gets the status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the reason phrase; empty when the service sent none.</summary>
    public string ReasonPhrase { get; }

    /// <summary>Gets the final URI, after redirects.</summary>
    public Uri RequestUri { get; }

    /// <summary>Gets the response and content headers the browser lets the page see.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>Gets the body, read once, as it arrives.</summary>
    public Stream Content { get; }

    /// <summary>Gets whether <see cref="StatusCode"/> is 200 to 299.</summary>
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;

    /// <summary>Gets the <c>Content-Type</c> header, or <see langword="null"/>.</summary>
    public string? ContentType => GetHeader("Content-Type");

    /// <summary>Gets the <c>Content-Length</c> header, or <see langword="null"/> when absent or not a number.</summary>
    public long? ContentLength =>
        long.TryParse(GetHeader("Content-Length"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long length)
            ? length
            : null;

    /// <summary>Gets the first value of a header, compared case-insensitively, or <see langword="null"/>.</summary>
    /// <param name="name">Header name.</param>
    public string? GetHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (KeyValuePair<string, string> header in Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <summary>Reads the whole body as bytes.</summary>
    /// <param name="cancellationToken">Aborts the read.</param>
    public async Task<byte[]> ReadAsByteArrayAsync(CancellationToken cancellationToken = default)
    {
        using MemoryStream buffer = ContentLength is { } length and > 0 and <= int.MaxValue ? new((int)length) : new();
        await Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Reads the whole body as text, decoded with the charset of <see cref="ContentType"/> (UTF-8 by default).</summary>
    /// <param name="cancellationToken">Aborts the read.</param>
    public async Task<string> ReadAsStringAsync(CancellationToken cancellationToken = default) =>
        SimulatedMediaTypes.GetEncoding(ContentType).GetString(await ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Reads the JSON body with a source-generated serializer (trim-safe).</summary>
    /// <param name="jsonTypeInfo">Type information of <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Aborts the read.</param>
    /// <exception cref="JsonException">The body is not valid JSON for <typeparamref name="T"/>.</exception>
    public async Task<T?> ReadFromJsonAsync<T>(JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return await JsonSerializer.DeserializeAsync(Content, jsonTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Throws when <see cref="IsSuccessStatusCode"/> is <see langword="false"/>, as <c>HttpResponseMessage</c> does.</summary>
    /// <exception cref="HttpRequestException">The status is not a success; its <c>StatusCode</c> is set.</exception>
    public KazoDirectHttpResponse EnsureSuccessStatusCode() =>
        IsSuccessStatusCode
            ? this
            : throw new HttpRequestException(
                $"Response status code does not indicate success: {StatusCode} ({ReasonPhrase}).",
                null,
                (System.Net.HttpStatusCode)StatusCode);

    /// <summary>Releases the body and the connection; an unread body is abandoned.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Content.Dispose();
        _owner?.Dispose();
    }

    /// <inheritdoc cref="Dispose"/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Content.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();
    }
}
