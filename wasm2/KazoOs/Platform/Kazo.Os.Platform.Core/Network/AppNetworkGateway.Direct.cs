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
    /// Gets the hosts the app declares for direct requests (<see cref="AppManifest.DirectNetworkHosts"/>),
    /// as written in its manifest; the gateway parses them with <see cref="DirectNetworkHosts"/>.
    /// Empty: every direct request is refused.
    /// </summary>
    public IReadOnlyList<string> DirectHosts { get; init; } = [];

    /// <summary>
    /// Gets how a direct request is sent, or <see langword="null"/> for the browser's <c>fetch</c>
    /// shared by every app. Tests point it at a fake handler; it must never reach the Internet in a test.
    /// </summary>
    public HttpMessageInvoker? DirectHttp { get; init; }

    /// <inheritdoc />
    /// <remarks>
    /// A network failure, a refusal of CORS or a mixed-content block is
    /// <see cref="KazoNetworkError.ConnectionFailed"/> with no more detail, as a browser tells a page;
    /// a timeout of the browser is <see cref="KazoNetworkError.TimedOut"/>. Cancelling
    /// <paramref name="cancellationToken"/> aborts the <c>fetch</c> and throws
    /// <see cref="OperationCanceledException"/>. Redirects are followed by the browser.
    /// </remarks>
    /// <exception cref="Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException">The app lacks <c>network.direct.access</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">The host is not declared in the app's manifest.</exception>
    /// <exception cref="ArgumentException">The URI is not absolute <c>https</c>, or a header is forbidden to a page.</exception>
    public async ValueTask<KazoNetworkResult<KazoDirectHttpResponse>> SendDirectAsync(
        KazoDirectHttpRequest request, CancellationToken cancellationToken = default)
    {
        AuthorizeDirect(request);
        using KernelMemoryCharge buffers = OpenSocket();
        KazoNetworkResult<KazoDirectHttpResponse> result = await KazoDirectHttpTransport
            .SendAsync(DirectHttp ?? KazoDirectHttpTransport.Shared, request, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _meter.Segment(KazoDirectHttpTransport.EstimateBytes(result.Value!), outbound: false, udp: false);
        }
        else
        {
            _meter.ConnectFailed(udp: false);
        }

        return result;
    }

    /// <summary>
    /// Checks that the app may send <paramref name="request"/> directly and counts it in the
    /// interface's statistics. The worker host calls it before it sends from the worker, so the same
    /// rules hold whichever thread the app runs on.
    /// </summary>
    /// <exception cref="Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException">The app lacks <c>network.direct.access</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">The host is not declared in the app's manifest.</exception>
    /// <exception cref="ArgumentException">The URI is not absolute <c>https</c>, or a header is forbidden to a page.</exception>
    internal void AuthorizeDirect(KazoDirectHttpRequest request)
    {
        KazoDirectHttpTransport.Validate(request);
        string host = request.RequestUri.IdnHost;
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        _capabilities.Require(AppCapabilities.NetworkDirectAccess);
        if (!DirectNetworkHosts.IsAllowed(DirectHosts, host))
        {
            throw new UnauthorizedAccessException(
                $"Host '{host}' is not declared in the app's directNetworkHosts; the OS refuses direct requests to it (ADR 0282).");
        }

        _meter.Segment(KazoDirectHttpTransport.EstimateBytes(request), outbound: true, udp: false);
    }
}
