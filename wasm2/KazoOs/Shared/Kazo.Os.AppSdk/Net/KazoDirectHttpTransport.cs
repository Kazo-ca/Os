using System.Net.Http;
using System.Net.Http.Headers;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// Sends a <see cref="KazoDirectHttpRequest"/> through an <see cref="HttpMessageInvoker"/>, which is
/// the browser's <c>fetch</c> under WebAssembly (ADR 0282 §4). Shared by the OS's gateway on the main
/// thread and by the worker host, which asks the OS for the permission and then sends from the worker
/// (a Web Worker has <c>fetch</c> too). Not for apps: they use <see cref="KazoDirectHttpClient"/>.
/// </summary>
internal static class KazoDirectHttpTransport
{
    /// <summary>
    /// Name of the browser option that hands the response body over as a stream instead of
    /// buffering it whole (<c>WebAssemblyEnableStreamingResponse</c> of the browser's HTTP handler).
    /// Set by name so the SDK needs no reference to the Blazor WebAssembly packages.
    /// </summary>
    private static readonly HttpRequestOptionsKey<bool> BrowserStreamingResponse = new("WebAssemblyEnableStreamingResponse");

    /// <summary>
    /// Request headers a page may not set (the Fetch standard's forbidden request-header names); the
    /// browser decides them itself. Refused up front, so an app does not believe it sent one.
    /// </summary>
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept-Charset", "Accept-Encoding", "Access-Control-Request-Headers", "Access-Control-Request-Method",
        "Connection", "Content-Length", "Cookie", "Cookie2", "Date", "DNT", "Expect", "Host", "Keep-Alive",
        "Origin", "Referer", "Set-Cookie", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Via"
    };

    private static HttpMessageInvoker? _shared;

    /// <summary>
    /// Gets or sets the invoker that sends direct requests when a gateway names none: one
    /// <see cref="HttpClientHandler"/>, the browser's <c>fetch</c>, which sends no cookie of the OS's
    /// origin to another site. Tests replace it with a fake handler; it never reaches the Internet in
    /// a test.
    /// </summary>
    internal static HttpMessageInvoker Shared
    {
        get => _shared ??= new HttpMessageInvoker(new HttpClientHandler(), disposeHandler: false);
        set => _shared = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Checks the URI is absolute <c>https</c> and every header name may be set by a page.
    /// </summary>
    /// <exception cref="ArgumentException">The URI is relative or not https, or a header is forbidden.</exception>
    internal static void Validate(KazoDirectHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = request.RequestUri;
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("A direct request needs an absolute https URI.", nameof(request));
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"A direct request is always https; '{uri.GetLeftPart(UriPartial.Authority)}' is not.", nameof(request));
        }

        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key))
            {
                throw new ArgumentException("A header has no name.", nameof(request));
            }

            if (ForbiddenHeaders.Contains(header.Key)
                || header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The header '{header.Key}' is set by the browser; a page cannot send it.", nameof(request));
            }
        }
    }

    /// <summary>
    /// Sends the request and returns as soon as the response headers arrive. A failure of the
    /// browser is a result, never an exception; cancelling <paramref name="cancellationToken"/> throws.
    /// </summary>
    /// <param name="invoker">The browser's <c>fetch</c>, or a test's fake.</param>
    /// <param name="request">A request already checked by <see cref="Validate"/> and the OS's policy.</param>
    /// <param name="cancellationToken">Aborts the <c>fetch</c>.</param>
    internal static async ValueTask<KazoNetworkResult<KazoDirectHttpResponse>> SendAsync(
        HttpMessageInvoker invoker, KazoDirectHttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        Uri uri = request.RequestUri;
        HttpResponseMessage? response = null;
        try
        {
            using HttpRequestMessage message = ToRequestMessage(request);
            response = await invoker.SendAsync(message, cancellationToken).ConfigureAwait(false);
            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            KazoDirectHttpResponse direct = new(
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                response.RequestMessage?.RequestUri ?? uri,
                ReadHeaders(response),
                body,
                response);
            response = null;
            return KazoNetworkResult<KazoDirectHttpResponse>.Success(direct);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return KazoNetworkResult<KazoDirectHttpResponse>.Failure(
                KazoNetworkError.TimedOut, $"Connection to {uri.Host} timed out");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
            or PlatformNotSupportedException or IOException)
        {
            // CORS, DNS, TLS and refused connections all reach a page as "TypeError: Failed to
            // fetch": the OS cannot tell them apart and does not pretend to.
            return KazoNetworkResult<KazoDirectHttpResponse>.Failure(
                KazoNetworkError.ConnectionFailed,
                $"Failed to connect to {uri.Host} (network error, or the service does not allow this origin)");
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>Approximate size of the request on the wire, for <c>/proc/net/dev</c>.</summary>
    internal static int EstimateBytes(KazoDirectHttpRequest request)
    {
        long size = request.Method.Method.Length + request.RequestUri.PathAndQuery.Length + 16;
        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            size += header.Key.Length + 4 + header.Value.Length;
        }

        size += request.Content?.Length ?? 0;
        return (int)Math.Min(size, int.MaxValue);
    }

    /// <summary>Approximate size of a response's status line and headers.</summary>
    internal static int EstimateBytes(KazoDirectHttpResponse response)
    {
        long size = 17 + response.ReasonPhrase.Length;
        foreach (KeyValuePair<string, string> header in response.Headers)
        {
            size += header.Key.Length + 4 + header.Value.Length;
        }

        return (int)Math.Min(size, int.MaxValue);
    }

    /// <summary>Builds the message the browser's handler sends; content headers go on the body.</summary>
    private static HttpRequestMessage ToRequestMessage(KazoDirectHttpRequest request)
    {
        HttpRequestMessage message = new(request.Method, request.RequestUri);
        message.Options.Set(BrowserStreamingResponse, true);
        if (request.Content is { } content)
        {
            HttpContent body = content.Stream is { } stream
                ? new StreamContent(new LeaveOpenStream(stream))
                : new ReadOnlyMemoryContent(content.Bytes ?? ReadOnlyMemory<byte>.Empty);
            body.Headers.TryAddWithoutValidation("Content-Type", content.ContentType);
            if (content.Length is { } length)
            {
                body.Headers.ContentLength = length;
            }

            message.Content = body;
        }

        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                // A content header (Content-Language, Content-MD5…) belongs to the body; without a
                // body there is nothing to describe. Content-Type always comes from the body.
                if (message.Content is not null
                    && !string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return message;
    }

    /// <summary>Response and content headers, one entry per value, in the order received.</summary>
    private static List<KeyValuePair<string, string>> ReadHeaders(HttpResponseMessage response)
    {
        List<KeyValuePair<string, string>> headers = [];
        Add(response.Headers);
        Add(response.Content.Headers);
        return headers;

        void Add(HttpHeaders source)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in source)
            {
                foreach (string value in header.Value)
                {
                    headers.Add(new(header.Key, value));
                }
            }
        }
    }

    /// <summary>
    /// Reads the app's upload stream without closing it: <see cref="StreamContent"/> disposes its
    /// stream with the message, but the stream belongs to the app (<see cref="KazoDirectHttpContent.FromStream"/>).
    /// </summary>
    private sealed class LeaveOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
