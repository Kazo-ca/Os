using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// HTTP client that reaches real web services directly through the browser's <c>fetch</c>
/// (ADR 0282 §4), without the simulated network and without the Kazo server: the way a drive
/// provider talks to its cloud service. Modeled on <see cref="HttpClient"/> with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>: a call returns as soon as the response
/// headers arrive and the body is read as a stream.
/// </summary>
/// <remarks>
/// <para>
/// The app needs the dangerous capability <c>network.direct.access</c> and declares every host it
/// joins in its manifest (<c>directNetworkHosts</c>: <c>api.example.com</c>, <c>*.example.org</c>);
/// the OS refuses any other host and every <c>http</c> address. <see cref="KazoHttpClient"/> is
/// unchanged and never goes direct: an app chooses this client explicitly.
/// </para>
/// <para>
/// The browser's limits apply: the service must allow the OS's origin in CORS (with a
/// cross-origin isolated page, ADR 0131), no cookie is sent, the headers a page may not set are
/// refused, and only the response headers the service exposes are visible. A network failure, a
/// CORS refusal and a TLS error look the same to a page: <see cref="KazoHttpRequestException"/>
/// with <see cref="KazoNetworkError.ConnectionFailed"/>. A response with an error status (404,
/// 500) is a response, not an exception.
/// </para>
/// </remarks>
public sealed class KazoDirectHttpClient : IDisposable
{
    private bool _disposed;
    private TimeSpan _timeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Gets or sets the base address a relative request URI is resolved against, as
    /// <c>HttpClient.BaseAddress</c>; it must be an absolute <c>https</c> URI.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Gets the headers added to every request before its own (<c>Authorization</c>, <c>Accept</c>…).</summary>
    public IDictionary<string, string> DefaultRequestHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets how long a call waits for the response headers, 100 seconds by default as
    /// <see cref="HttpClient.Timeout"/>; <see cref="Timeout.InfiniteTimeSpan"/> waits forever. Reading
    /// the body is bounded by the caller's cancellation token only.
    /// </summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        set
        {
            if (value != System.Threading.Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive or infinite.");
            }

            _timeout = value;
        }
    }

    /// <summary>Sends a <c>GET</c> request.</summary>
    /// <inheritdoc cref="SendAsync(KazoDirectHttpRequest, CancellationToken)" path="/exception"/>
    public Task<KazoDirectHttpResponse> GetAsync(string requestUri, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, requestUri, content: null, cancellationToken);

    /// <summary>Sends a <c>GET</c> request and returns the body as text, throwing on an error status.</summary>
    /// <exception cref="HttpRequestException">No response came back, or its status is not a success.</exception>
    public async Task<string> GetStringAsync(string requestUri, CancellationToken cancellationToken = default)
    {
        using KazoDirectHttpResponse response = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return await response.EnsureSuccessStatusCode().ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a <c>GET</c> request and returns the body bytes, throwing on an error status.</summary>
    /// <exception cref="HttpRequestException">No response came back, or its status is not a success.</exception>
    public async Task<byte[]> GetByteArrayAsync(string requestUri, CancellationToken cancellationToken = default)
    {
        using KazoDirectHttpResponse response = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return await response.EnsureSuccessStatusCode().ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a <c>GET</c> request and deserializes the JSON body, throwing on an error status.</summary>
    /// <exception cref="HttpRequestException">No response came back, or its status is not a success.</exception>
    /// <exception cref="JsonException">The body is not valid JSON for <typeparamref name="T"/>.</exception>
    public async Task<T?> GetFromJsonAsync<T>(string requestUri, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default)
    {
        using KazoDirectHttpResponse response = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return await response.EnsureSuccessStatusCode().ReadFromJsonAsync(jsonTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a <c>POST</c> request with a body.</summary>
    /// <inheritdoc cref="SendAsync(KazoDirectHttpRequest, CancellationToken)" path="/exception"/>
    public Task<KazoDirectHttpResponse> PostAsync(string requestUri, KazoDirectHttpContent? content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, requestUri, content, cancellationToken);

    /// <summary>Sends a <c>POST</c> request with <paramref name="value"/> serialized as JSON (trim-safe).</summary>
    /// <inheritdoc cref="SendAsync(KazoDirectHttpRequest, CancellationToken)" path="/exception"/>
    public Task<KazoDirectHttpResponse> PostAsJsonAsync<T>(
        string requestUri, T value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default) =>
        PostAsync(requestUri, KazoDirectHttpContent.FromJson(value, jsonTypeInfo), cancellationToken);

    /// <summary>Sends a <c>PUT</c> request with a body.</summary>
    /// <inheritdoc cref="SendAsync(KazoDirectHttpRequest, CancellationToken)" path="/exception"/>
    public Task<KazoDirectHttpResponse> PutAsync(string requestUri, KazoDirectHttpContent? content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, requestUri, content, cancellationToken);

    /// <summary>Sends a <c>PATCH</c> request with a body.</summary>
    /// <inheritdoc cref="SendAsync(KazoDirectHttpRequest, CancellationToken)" path="/exception"/>
    public Task<KazoDirectHttpResponse> PatchAsync(string requestUri, KazoDirectHttpContent? content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, requestUri, content, cancellationToken);

    /// <summary>Sends a <c>DELETE</c> request.</summary>
    /// <inheritdoc cref="SendAsync(KazoDirectHttpRequest, CancellationToken)" path="/exception"/>
    public Task<KazoDirectHttpResponse> DeleteAsync(string requestUri, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, requestUri, content: null, cancellationToken);

    /// <summary>Sends a request with any method and an optional body.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="requestUri">Absolute <c>https</c> URI, or relative to <see cref="BaseAddress"/>.</param>
    /// <param name="content">The body, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <inheritdoc cref="SendAsync(KazoDirectHttpRequest, CancellationToken)" path="/exception"/>
    public Task<KazoDirectHttpResponse> SendAsync(
        HttpMethod method, string requestUri, KazoDirectHttpContent? content = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUri);
        Uri uri = Uri.TryCreate(requestUri, UriKind.RelativeOrAbsolute, out Uri? parsed)
            ? parsed
            : throw new ArgumentException($"'{requestUri}' is not a URI.", nameof(requestUri));
        return SendAsync(new KazoDirectHttpRequest(method, uri) { Content = content }, cancellationToken);
    }

    /// <summary>
    /// Sends a request and returns as soon as the response headers arrive; dispose the response
    /// once its body is read.
    /// </summary>
    /// <param name="request">The request; its headers come after <see cref="DefaultRequestHeaders"/>.</param>
    /// <param name="cancellationToken">Aborts the request; the browser's <c>fetch</c> is aborted too.</param>
    /// <exception cref="ArgumentException">
    /// The URI is not absolute <c>https</c> (after <see cref="BaseAddress"/>), or a header is one only the
    /// browser may set (<c>Host</c>, <c>Cookie</c>, <c>Origin</c>, <c>Sec-*</c>…).
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The app lacks <c>network.direct.access</c>, or the host is not declared in its manifest's
    /// <c>directNetworkHosts</c>.
    /// </exception>
    /// <exception cref="KazoHttpRequestException">
    /// No response came back: network failure, CORS refusal or TLS error
    /// (<see cref="KazoNetworkError.ConnectionFailed"/>).
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// <see cref="Timeout"/> elapsed before the headers (its inner exception is a <see cref="TimeoutException"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="NotSupportedException">The app's context has no network gateway that can go direct.</exception>
    public async Task<KazoDirectHttpResponse> SendAsync(KazoDirectHttpRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        KazoDirectHttpRequest prepared = Prepare(request);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_timeout);
        }

        KazoNetworkResult<KazoDirectHttpResponse> result;
        try
        {
            result = await KazoNetworkErrorTranslator
                .CallAsync(gateway => gateway.SendDirectAsync(prepared, timeout.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            // As HttpClient: the exception names the caller's token, not the linked one.
            throw new TaskCanceledException(exception.Message, exception, cancellationToken);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"The request was canceled due to the configured Timeout of {_timeout.TotalSeconds} seconds elapsing.",
                new TimeoutException(exception.Message, exception));
        }

        return KazoNetworkErrorTranslator.UnwrapHttp(result, prepared.RequestUri);
    }

    /// <summary>Marks the client disposed; responses already returned stay readable.</summary>
    public void Dispose() => _disposed = true;

    /// <summary>Resolves the URI, checks it is https, and puts the default headers first.</summary>
    private KazoDirectHttpRequest Prepare(KazoDirectHttpRequest request)
    {
        Uri uri = request.RequestUri;
        if (!uri.IsAbsoluteUri)
        {
            uri = BaseAddress is { IsAbsoluteUri: true } baseAddress
                ? new Uri(baseAddress, uri)
                : throw new ArgumentException($"'{uri}' is relative and the client has no BaseAddress.", nameof(request));
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"'{uri}' is not an https URI; a direct request is always https.", nameof(request));
        }

        KazoDirectHttpRequest prepared = new(request.Method, uri) { Content = request.Content };
        foreach (KeyValuePair<string, string> header in DefaultRequestHeaders)
        {
            prepared.Headers.Add(header);
        }

        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            prepared.Headers.Add(header);
        }

        KazoDirectHttpTransport.Validate(prepared);
        return prepared;
    }
}
