using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// Stops a request before it reaches <c>fetch</c> when the browser reports no network connection at
/// all (<c>navigator.onLine</c> is <see langword="false"/>, read through the host's
/// <c>networkState.js</c> module), and fails it the way an unreachable network does: an
/// <see cref="HttpRequestException"/> with <see cref="HttpRequestError.ConnectionError"/>.
/// </summary>
/// <remarks>
/// The OS's background probes (the served <c>version.json</c> of ADR 0174, the app catalogs of
/// ADR 0188) already treat an unreachable network as "nothing new". Offline, letting them call
/// <c>fetch</c> anyway logs « Failed to load resource: net::ERR_FAILED » in the console for each try,
/// which reads like a broken release (#493). A real machine with its cable unplugged does not dial
/// out either. When the module cannot be read (no JavaScript runtime, a prerender), the request goes
/// on as before.
/// </remarks>
internal sealed class BrowserOfflineHandler : DelegatingHandler
{
    private const string ModulePath = "./_content/Kazo.Os.Ecosystem.Host/networkState.js";

    private readonly IJSRuntime _runtime;
    private IJSObjectReference? _module;

    /// <summary>Creates the handler in front of the browser's own HTTP handler.</summary>
    /// <param name="runtime">Reads <c>navigator.onLine</c>.</param>
    public BrowserOfflineHandler(IJSRuntime runtime)
        : this(runtime, new HttpClientHandler())
    {
    }

    /// <summary>Creates the handler in front of <paramref name="innerHandler"/> (tests).</summary>
    /// <param name="runtime">Reads <c>navigator.onLine</c>.</param>
    /// <param name="innerHandler">Sends the requests that go out.</param>
    internal BrowserOfflineHandler(IJSRuntime runtime, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (await IsOfflineAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new HttpRequestException(
                HttpRequestError.ConnectionError,
                $"No network connection: the browser is offline, {request.RequestUri} was not requested.");
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // The module is imported once and kept; any interop failure counts as "online" so the request
    // behaves exactly as it did without this handler.
    private async Task<bool> IsOfflineAsync(CancellationToken cancellationToken)
    {
        try
        {
            _module ??= await _runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
            return await _module.InvokeAsync<bool>("isOffline", cancellationToken).ConfigureAwait(false);
        }
        catch (JSException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // No JavaScript runtime yet (prerender) or none at all.
            return false;
        }
    }
}
