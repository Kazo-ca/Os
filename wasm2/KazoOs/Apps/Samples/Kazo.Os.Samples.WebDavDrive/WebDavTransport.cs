using System.Net.Http;
using Kazo.Os.AppSdk.Net;

namespace Kazo.Os.Samples.WebDavDrive;

/// <summary>One request to the WebDAV server: what <see cref="WebDavDriveFolder"/> builds, whatever sends it.</summary>
/// <param name="Method">The HTTP method (<c>PROPFIND</c>, <c>PUT</c>…).</param>
/// <param name="Uri">The absolute <c>https</c> address of the resource.</param>
/// <param name="Headers">The request headers (<c>Authorization</c>, <c>Depth</c>, <c>If-Match</c>…).</param>
/// <param name="Body">The body, or <see langword="null"/>.</param>
/// <param name="ContentType">The <c>Content-Type</c> of <paramref name="Body"/>.</param>
public sealed record WebDavRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    ReadOnlyMemory<byte>? Body = null,
    string ContentType = "application/octet-stream");

/// <summary>The answer of the WebDAV server, with its body still to read.</summary>
public sealed class WebDavResponse : IAsyncDisposable
{
    private readonly IAsyncDisposable? _owner;

    /// <summary>Creates a response.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The response headers the browser lets the page see.</param>
    /// <param name="body">The body, read as it arrives.</param>
    /// <param name="owner">What the response disposes with it (the transport's own response), or <see langword="null"/>.</param>
    public WebDavResponse(int status, IReadOnlyList<KeyValuePair<string, string>> headers, Stream body, IAsyncDisposable? owner = null)
    {
        Status = status;
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        _owner = owner;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int Status { get; }

    /// <summary>Gets the response headers.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>Gets the body.</summary>
    public Stream Body { get; }

    /// <summary>Gets the first value of header <paramref name="name"/>, compared without case, or <see langword="null"/>.</summary>
    /// <param name="name">The header's name.</param>
    public string? Header(string name)
    {
        foreach (KeyValuePair<string, string> header in Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync().ConfigureAwait(false);
        if (_owner is not null)
        {
            await _owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Sends a <see cref="WebDavRequest"/>. In Kazo OS it is <see cref="DirectHttpWebDavTransport"/>
/// (the browser's <c>fetch</c>, ADR 0282 §4); tests give a local server instead, so they never reach a real one.
/// </summary>
public interface IWebDavTransport
{
    /// <summary>Sends the request and returns as soon as the headers arrive.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <returns>The response; an error status is a response, not an exception.</returns>
    /// <exception cref="HttpRequestException">No response came back (network down, CORS refused, TLS error).</exception>
    Task<WebDavResponse> SendAsync(WebDavRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The transport of the provider in Kazo OS: <see cref="KazoDirectHttpClient"/>, which sends
/// straight from the browser to the server the user typed, without the Kazo server (ADR 0282 §4).
/// The server must allow the OS's origin in CORS, with the WebDAV methods and headers.
/// </summary>
public sealed class DirectHttpWebDavTransport : IWebDavTransport, IDisposable
{
    private readonly KazoDirectHttpClient _client = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <inheritdoc />
    public async Task<WebDavResponse> SendAsync(WebDavRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        KazoDirectHttpRequest message = new(request.Method, request.Uri);
        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            message.Headers.Add(header);
        }

        if (request.Body is { } body)
        {
            message.Content = KazoDirectHttpContent.FromBytes(body, request.ContentType);
        }

        KazoDirectHttpResponse response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return new WebDavResponse(response.StatusCode, response.Headers, response.Content, response);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
