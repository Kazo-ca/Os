using System.Net.Http;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// Direct web requests (ADR 0282 §4): the request leaves through the browser's <c>fetch</c>
/// (the <see cref="HttpClient"/> of .NET under WebAssembly), without the simulated network and
/// without the Kazo server's relay. Only for an app that holds <c>network.direct.access</c>, only
/// in <c>https</c>, and only to a host its manifest declares in <c>directNetworkHosts</c>.
/// </summary>
public sealed partial class AppNetworkGateway
{
    /// <summary>
    /// Name of the browser option that hands the response body over as a stream instead of
    /// buffering it whole (<c>WebAssemblyEnableStreamingResponse</c> of the browser's HTTP handler).
    /// Set by name so the platform needs no reference to the Blazor WebAssembly packages.
    /// </summary>
    private static readonly HttpRequestOptionsKey<bool> BrowserStreamingResponse = new("WebAssemblyEnableStreamingResponse");

    /// <summary>
    /// The invoker shared by every app's direct requests: one <see cref="HttpClientHandler"/>, which
    /// is the browser's <c>fetch</c> under WebAssembly. It sends no cookie of the OS's own origin
    /// to another site, as a page's cross-origin <c>fetch</c>.
    /// </summary>
    private static readonly Lazy<HttpMessageInvoker> SharedDirectInvoker =
        new(static () => new HttpMessageInvoker(new HttpClientHandler(), disposeHandler: false));

    /// <summary>
    /// Gets the hosts the app declares for direct requests (<see cref="AppManifest.DirectNetworkHosts"/>),
    /// as written in its manifest; the gateway parses them with <see cref="DirectNetworkHosts"/>.
    /// Empty: every direct request is refused.
    /// </summary>
    public IReadOnlyList<string> DirectHosts { get; init; } = [];

    /// <summary>
    /// Gets how a direct request is sent: by default the browser's <c>fetch</c>. Tests point it at
    /// a local fake server; it must never reach the Internet in a test.
    /// </summary>
    public HttpMessageInvoker DirectHttp { get; init; } = SharedDirectInvoker.Value;

    /// <summary>
    /// Sends one direct request and returns as soon as the response headers arrive; the caller owns
    /// the response and reads its body as a stream. Redirects are followed by the browser.
    /// </summary>
    /// <remarks>
    /// A network failure, a refusal of CORS or a mixed-content block is
    /// <see cref="KazoNetworkError.ConnectionFailed"/> with no more detail, as a browser tells a page;
    /// a timeout of the browser is <see cref="KazoNetworkError.TimedOut"/>. Cancelling
    /// <paramref name="cancellationToken"/> aborts the <c>fetch</c> and throws
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    /// <param name="request">The request; its URI must be absolute <c>https</c>.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <exception cref="Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException">The app lacks <c>network.direct.access</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">The host is not declared in the app's manifest.</exception>
    /// <exception cref="ArgumentException">The URI is not absolute <c>https</c>.</exception>
    internal async ValueTask<KazoNetworkResult<HttpResponseMessage>> SendDirectAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = request.RequestUri is { IsAbsoluteUri: true } absolute
            ? absolute
            : throw new ArgumentException("A direct request needs an absolute https URI.", nameof(request));
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"A direct request is always https; '{uri.GetLeftPart(UriPartial.Authority)}' is not.", nameof(request));
        }

        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        _capabilities.Require(AppCapabilities.NetworkDirectAccess);
        if (!DirectNetworkHosts.IsAllowed(DirectHosts, uri.IdnHost))
        {
            throw new UnauthorizedAccessException(
                $"Host '{uri.IdnHost}' is not declared in the app's directNetworkHosts; the OS refuses direct requests to it (ADR 0282).");
        }

        request.Options.Set(BrowserStreamingResponse, true);
        _meter.Segment(EstimateRequestBytes(request), outbound: true, udp: false);
        using KernelMemoryCharge buffers = OpenSocket();
        try
        {
            HttpResponseMessage response = await DirectHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _meter.Segment(EstimateHeaderBytes(response), outbound: false, udp: false);
            return KazoNetworkResult<HttpResponseMessage>.Success(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _meter.ConnectFailed(udp: false);
            return KazoNetworkResult<HttpResponseMessage>.Failure(
                KazoNetworkError.TimedOut, $"Connection to {uri.Host} timed out");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
            or PlatformNotSupportedException or IOException)
        {
            // CORS, DNS, TLS and refused connections all reach a page as "TypeError: Failed to
            // fetch": the OS cannot tell them apart and does not pretend to.
            _meter.ConnectFailed(udp: false);
            return KazoNetworkResult<HttpResponseMessage>.Failure(
                KazoNetworkError.ConnectionFailed,
                $"Failed to connect to {uri.Host} (network error, or the service does not allow this origin)");
        }
    }

    /// <summary>Approximate size of the request on the wire, for <c>/proc/net/dev</c>.</summary>
    private static int EstimateRequestBytes(HttpRequestMessage request)
    {
        long size = request.Method.Method.Length + (request.RequestUri?.PathAndQuery.Length ?? 1) + 16;
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            size += header.Key.Length + 4 + header.Value.Sum(static value => value.Length);
        }

        size += request.Content?.Headers.ContentLength ?? 0;
        return (int)Math.Min(size, int.MaxValue);
    }

    /// <summary>Approximate size of the status line and headers of a response.</summary>
    private static int EstimateHeaderBytes(HttpResponseMessage response)
    {
        long size = 17 + (response.ReasonPhrase?.Length ?? 0);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            size += header.Key.Length + 4 + header.Value.Sum(static value => value.Length);
        }

        return (int)Math.Min(size, int.MaxValue);
    }
}
