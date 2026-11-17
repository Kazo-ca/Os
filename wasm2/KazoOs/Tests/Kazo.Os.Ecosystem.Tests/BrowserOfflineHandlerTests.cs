using System.Net;
using Kazo.Os.Ecosystem;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// #493: with no network at all, the OS's probes fail as "unreachable" without calling fetch, so the
/// console of an offline boot has no « Failed to load resource » error.
/// </summary>
public sealed class BrowserOfflineHandlerTests
{
    [Fact]
    public async Task An_offline_browser_fails_the_request_as_a_connection_error_without_sending_it()
    {
        // Arrange
        CountingHandler network = new();
        using HttpClient http = new(new BrowserOfflineHandler(new NetworkStateRuntime(offline: true), network));

        // Act
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetAsync(new Uri("https://example.test/version.json")));

        // Assert
        Assert.Equal(HttpRequestError.ConnectionError, failure.HttpRequestError);
        Assert.Equal(0, network.Sent);
    }

    [Fact]
    public async Task An_online_browser_sends_the_request()
    {
        // Arrange
        CountingHandler network = new();
        using HttpClient http = new(new BrowserOfflineHandler(new NetworkStateRuntime(offline: false), network));

        // Act
        using HttpResponseMessage response = await http.GetAsync(new Uri("https://example.test/version.json"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, network.Sent);
    }

    [Fact]
    public async Task A_network_state_that_cannot_be_read_lets_the_request_go_as_before()
    {
        // Arrange
        CountingHandler network = new();
        using HttpClient http = new(new BrowserOfflineHandler(new NetworkStateRuntime(offline: null), network));

        // Act
        using HttpResponseMessage response = await http.GetAsync(new Uri("https://example.test/catalog.json"));

        // Assert
        Assert.Equal(1, network.Sent);
    }

    [Fact]
    public async Task The_module_is_imported_once_for_every_request()
    {
        // Arrange
        NetworkStateRuntime runtime = new(offline: false);
        using HttpClient http = new(new BrowserOfflineHandler(runtime, new CountingHandler()));

        // Act
        (await http.GetAsync(new Uri("https://example.test/a"))).Dispose();
        (await http.GetAsync(new Uri("https://example.test/b"))).Dispose();

        // Assert
        Assert.Equal(1, runtime.Imports);
    }

    /// <summary>Answers every request with 200 and counts them.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// Imports a networkState.js stand-in whose <c>isOffline</c> answers <paramref name="offline"/>;
    /// <see langword="null"/> makes the call fail like a page with no such module.
    /// </summary>
    private sealed class NetworkStateRuntime(bool? offline) : IJSRuntime
    {
        public int Imports { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            Assert.Equal("./_content/Kazo.Os.Ecosystem.Host/networkState.js", Assert.Single(args!));
            Imports++;
            return new ValueTask<TValue>((TValue)(object)new NetworkStateModule(offline));
        }
    }

    private sealed class NetworkStateModule(bool? offline) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("isOffline", identifier);
            return offline is { } value
                ? new ValueTask<TValue>((TValue)(object)value)
                : throw new JSException("isOffline is not a function");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
