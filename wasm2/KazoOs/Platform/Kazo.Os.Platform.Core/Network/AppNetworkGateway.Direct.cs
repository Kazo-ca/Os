using System.Net.Http;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// Direct web requests (ADR 0282 §4): the request leaves through the browser's <c>fetch</c>
/// (the <see cref="HttpClient"/> of .NET under WebAssembly), without the simulated network and
/// without the Kazo server's relay. Only in <c>https</c>, and only to a host its manifest declares
/// in <c>directNetworkHosts</c> (with <c>network.direct.access</c>) or that its drive providers may
/// reach (with <c>drives.provide</c>, ADR 0284).
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
    /// Gets the hosts the drive providers of the app declare (<see cref="AppDriveProviders.HostsOf"/>):
    /// a feature that holds <c>drives.provide</c> reaches them without <c>network.direct.access</c>
    /// (ADR 0284 §1). Empty for an app that provides no drive.
    /// </summary>
    public IReadOnlyList<string> ProviderHosts { get; init; } = [];

    /// <summary>
    /// Gets the servers users typed for the app's open drives (ADR 0284 §2), or <see langword="null"/>
    /// when the page keeps none (tests, an app that provides no drive).
    /// </summary>
    public DriveServerHosts? ServerHosts { get; init; }

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
    /// <exception cref="Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException">The app lacks the capability the host needs.</exception>
    /// <exception cref="UnauthorizedAccessException">The host is not declared in the app's manifest nor typed for one of its open drives.</exception>
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
    /// <exception cref="Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException">The app lacks the capability the host needs.</exception>
    /// <exception cref="UnauthorizedAccessException">The host is not declared in the app's manifest nor typed for one of its open drives.</exception>
    /// <exception cref="ArgumentException">The URI is not absolute <c>https</c>, or a header is forbidden to a page.</exception>
    internal void AuthorizeDirect(KazoDirectHttpRequest request)
    {
        KazoDirectHttpTransport.Validate(request);
        string host = request.RequestUri.IdnHost;
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        RequireDirectHost(host);

        _meter.Segment(KazoDirectHttpTransport.EstimateBytes(request), outbound: true, udp: false);
    }

    /// <summary>
    /// ADR 0284 §1: a host of <c>directNetworkHosts</c> needs <c>network.direct.access</c>; a host of
    /// the app's drive providers, or a server a user typed for one of its open drives (§2), needs
    /// <c>drives.provide</c>; any other host is refused. An app that holds neither capability is
    /// refused for the capability, as before ADR 0284.
    /// </summary>
    private void RequireDirectHost(string host)
    {
        if (DirectNetworkHosts.IsAllowed(DirectHosts, host))
        {
            _capabilities.Require(AppCapabilities.NetworkDirectAccess);
            return;
        }

        if (DirectNetworkHosts.IsAllowed(ProviderHosts, host) || ServerHosts?.IsAllowed(_appId, host) == true)
        {
            _capabilities.Require(AppCapabilities.DrivesProvide);
            return;
        }

        if (!_capabilities.Evaluate(AppCapabilities.DrivesProvide).Granted)
        {
            _capabilities.Require(AppCapabilities.NetworkDirectAccess);
        }

        throw new UnauthorizedAccessException(
            $"Host '{host}' is not declared in the app's directNetworkHosts nor by its drive providers; the OS refuses direct requests to it (ADR 0282, ADR 0284).");
    }
}
