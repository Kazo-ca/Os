using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Ping;
using Kazo.Os.Platform.Core.Network;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// ADR 0282 §4: <see cref="KazoDirectHttpClient"/> reaches a real web service through the browser's
/// <c>fetch</c>, only with <c>network.direct.access</c> and only to the hosts the manifest declares.
/// The "browser" here is a fake handler: no test reaches the Internet.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    private static readonly string[] DeclaredDirectHosts = ["api.example.com", "*.content.example.org"];

    /// <summary>Enters an app whose direct requests go to <paramref name="browser"/>.</summary>
    private void EnterDirectApp(FakeBrowser browser, params string[] deniedCapabilities)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PingApp());
        KazoExecutionContext.Enter(new StubAppExecutionContext(
            manifest,
            new AppNetworkGateway(_network, new GrantAllCapabilityChecker(deniedCapabilities), manifest.Id, _serverConnection, _proxy)
            {
                DirectHosts = DeclaredDirectHosts,
                DirectHttp = new HttpMessageInvoker(browser)
            }));
    }

    /// <summary>Enters a drive-providing feature (ADR 0284): no directNetworkHosts, provider hosts and typed servers.</summary>
    private string EnterProviderApp(FakeBrowser browser, DriveServerHosts servers, params string[] deniedCapabilities)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PingApp());
        KazoExecutionContext.Enter(new StubAppExecutionContext(
            manifest,
            new AppNetworkGateway(_network, new GrantAllCapabilityChecker(deniedCapabilities), manifest.Id, _serverConnection, _proxy)
            {
                ProviderHosts = ["api.drive.example.com"],
                ServerHosts = servers,
                DirectHttp = new HttpMessageInvoker(browser)
            }));
        return manifest.Id;
    }

    [Fact]
    public async Task A_provider_host_needs_drives_provide_and_not_network_direct_access()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "ok"));
        EnterProviderApp(browser, new DriveServerHosts(), AppCapabilities.NetworkDirectAccess);
        using KazoDirectHttpClient client = new();

        string body = await client.GetStringAsync("https://api.drive.example.com/list");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetAsync("https://dav.example.org/"));

        Assert.Equal("ok", body);
        Assert.Single(browser.Requests);
    }

    [Fact]
    public async Task Without_drives_provide_a_provider_host_is_refused()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "never"));
        EnterProviderApp(browser, new DriveServerHosts(), AppCapabilities.DrivesProvide, AppCapabilities.NetworkDirectAccess);
        using KazoDirectHttpClient client = new();

        UnauthorizedAccessException refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.GetAsync("https://api.drive.example.com/list"));

        Assert.Contains(AppCapabilities.DrivesProvide, refused.Message);
        Assert.Empty(browser.Requests);
    }

    [Fact]
    public async Task A_typed_server_is_reached_only_while_one_of_the_feature_s_drives_holds_it()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "ok"));
        DriveServerHosts servers = new();
        string appId = EnterProviderApp(browser, servers, AppCapabilities.NetworkDirectAccess);
        using KazoDirectHttpClient client = new();
        IDisposable first = servers.Allow(appId, "dav.example.org");
        IDisposable second = servers.Allow(appId, "DAV.example.org");
        using IDisposable other = servers.Allow("org.example.other", "cloud.example.net");

        await client.GetStringAsync("https://dav.example.org/remote.php/dav/");
        first.Dispose();
        first.Dispose();
        await client.GetStringAsync("https://dav.example.org/remote.php/dav/");
        second.Dispose();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetAsync("https://dav.example.org/remote.php/dav/"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetAsync("https://cloud.example.net/"));

        Assert.Equal(2, browser.Requests.Count);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("192.168.1.20")]
    [InlineData("*.example.org")]
    [InlineData("nas")]
    public void A_server_the_os_may_not_allow_takes_no_lease(string host)
    {
        Assert.Throws<ArgumentException>(() => new DriveServerHosts().Allow("org.example.drive", host));
    }

    [Fact]
    public async Task A_declared_host_is_reached_and_answers_with_status_headers_and_body()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "hello", ("X-Request-Id", "42")));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();

        using KazoDirectHttpResponse response = await client.GetAsync("https://api.example.com/v1/files?limit=2");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("42", response.GetHeader("x-request-id"));
        Assert.StartsWith("text/plain", response.ContentType);
        Assert.Equal("hello", await response.ReadAsStringAsync());
        HttpRequestMessage sent = Assert.Single(browser.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://api.example.com/v1/files?limit=2", sent.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_subdomain_of_a_wildcard_is_reached_but_not_the_wildcard_s_own_domain()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "ok"));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();

        string body = await client.GetStringAsync("https://dl.eu.content.example.org/blob");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetAsync("https://content.example.org/blob"));

        Assert.Equal("ok", body);
        Assert.Single(browser.Requests);
    }

    [Fact]
    public async Task An_undeclared_host_is_refused_before_anything_leaves()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "never"));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();

        UnauthorizedAccessException refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.GetAsync("https://evil.example.net/steal"));

        Assert.Contains("evil.example.net", refused.Message);
        Assert.Empty(browser.Requests);
    }

    [Fact]
    public async Task Without_the_capability_even_a_declared_host_is_refused()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "never"));
        EnterDirectApp(browser, AppCapabilities.NetworkDirectAccess);
        using KazoDirectHttpClient client = new();

        UnauthorizedAccessException refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.GetAsync("https://api.example.com/v1"));

        Assert.Contains(AppCapabilities.NetworkDirectAccess, refused.Message);
        Assert.Empty(browser.Requests);
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("/v1/relative-without-base")]
    public async Task Only_absolute_https_addresses_are_accepted(string address)
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "never"));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync(address));

        Assert.Empty(browser.Requests);
    }

    [Theory]
    [InlineData("Cookie")]
    [InlineData("Host")]
    [InlineData("Origin")]
    [InlineData("Sec-Fetch-Mode")]
    [InlineData("Proxy-Authorization")]
    public async Task A_header_only_the_browser_may_set_is_refused(string name)
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "never"));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();
        client.DefaultRequestHeaders[name] = "x";

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync("https://api.example.com/v1"));

        Assert.Empty(browser.Requests);
    }

    [Fact]
    public async Task A_json_post_carries_the_default_headers_the_body_and_its_type()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.Created, "{\"id\":7,\"name\":\"report.txt\"}", ("Content-Type", "application/json")));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new() { BaseAddress = new Uri("https://api.example.com/v2/") };
        client.DefaultRequestHeaders["Authorization"] = "Bearer token-from-the-os";

        using KazoDirectHttpResponse response = await client.PostAsJsonAsync(
            "files", new DirectFile(0, "report.txt"), DirectJsonContext.Default.DirectFile);
        DirectFile? created = await response.EnsureSuccessStatusCode().ReadFromJsonAsync(DirectJsonContext.Default.DirectFile);

        Assert.Equal(new DirectFile(7, "report.txt"), created);
        HttpRequestMessage sent = Assert.Single(browser.Requests);
        Assert.Equal("https://api.example.com/v2/files", sent.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer token-from-the-os", sent.Headers.Authorization!.ToString());
        Assert.Equal("application/json; charset=utf-8", sent.Content!.Headers.ContentType!.ToString());
        Assert.Equal("{\"id\":0,\"name\":\"report.txt\"}", browser.Bodies.Single());
    }

    [Fact]
    public async Task A_stream_body_is_sent_as_it_is_read()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "stored"));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();
        using MemoryStream upload = new(Encoding.UTF8.GetBytes("chunk of a large file"));

        using KazoDirectHttpResponse response = await client.PutAsync(
            "https://api.example.com/upload", KazoDirectHttpContent.FromStream(upload, "application/octet-stream", upload.Length));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("chunk of a large file", browser.Bodies.Single());
        Assert.Equal(upload.Length, browser.Requests.Single().Content!.Headers.ContentLength);
    }

    [Fact]
    public async Task An_error_status_is_a_response_and_only_EnsureSuccessStatusCode_throws()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.NotFound, "no such file"));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();

        using KazoDirectHttpResponse response = await client.GetAsync("https://api.example.com/missing");
        HttpRequestException thrown = Assert.Throws<HttpRequestException>(() => response.EnsureSuccessStatusCode());
        HttpRequestException viaString = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync("https://api.example.com/missing"));

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, thrown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaString.StatusCode);
    }

    [Fact]
    public async Task The_response_returns_with_its_headers_while_the_body_is_still_arriving()
    {
        TaskCompletionSource rest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeBrowser browser = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TwoPartStream("first|"u8.ToArray(), "second"u8.ToArray(), rest.Task))
        });
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();

        using KazoDirectHttpResponse response = await client.GetAsync("https://api.example.com/stream")
            .WaitAsync(TimeSpan.FromSeconds(10));
        byte[] first = new byte[6];
        int read = await response.Content.ReadAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        rest.SetResult();
        string remainder = await new StreamReader(response.Content).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("first|", Encoding.UTF8.GetString(first, 0, read));
        Assert.Equal("second", remainder);
        Assert.True(browser.Requests.Single().Options.TryGetValue(
            new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), out bool streaming) && streaming);
    }

    [Fact]
    public async Task Cancelling_aborts_the_request()
    {
        FakeBrowser browser = new(_ => throw new InvalidOperationException("not reached"))
        {
            Hang = true
        };
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();
        using CancellationTokenSource cancel = new();

        Task<KazoDirectHttpResponse> pending = client.GetAsync("https://api.example.com/slow", cancel.Token);
        await browser.Started.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        OperationCanceledException canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancel.Token, canceled.CancellationToken);
        Assert.True(browser.SawCancellation);
    }

    [Fact]
    public async Task The_client_timeout_throws_TaskCanceledException_with_a_TimeoutException_inside()
    {
        FakeBrowser browser = new(_ => throw new InvalidOperationException("not reached")) { Hang = true };
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new() { Timeout = TimeSpan.FromMilliseconds(100) };

        TaskCanceledException timedOut = await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync("https://api.example.com/slow"));

        Assert.IsType<TimeoutException>(timedOut.InnerException);
    }

    [Fact]
    public async Task A_refusal_of_the_browser_is_a_connection_failure_as_for_KazoHttpClient()
    {
        // CORS, DNS and TLS failures all reach a page as "TypeError: Failed to fetch".
        FakeBrowser browser = new(_ => throw new HttpRequestException("TypeError: Failed to fetch"));
        EnterDirectApp(browser);
        using KazoDirectHttpClient client = new();

        KazoHttpRequestException failed = await Assert.ThrowsAsync<KazoHttpRequestException>(
            () => client.GetAsync("https://api.example.com/v1"));

        Assert.Equal(KazoNetworkError.ConnectionFailed, failed.NetworkError);
        Assert.Equal(HttpRequestError.ConnectionError, failed.HttpRequestError);
        Assert.Contains("api.example.com", failed.Message);
    }

    [Fact]
    public async Task KazoHttpClient_never_goes_direct_even_for_a_declared_host()
    {
        FakeBrowser browser = new(_ => FakeBrowser.Text(HttpStatusCode.OK, "direct"));
        EnterDirectApp(browser);
        using KazoHttpClient client = new();

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync("https://api.example.com/v1"));

        Assert.Empty(browser.Requests);
    }

    /// <summary>A JSON body of the tests.</summary>
    internal sealed record DirectFile(int Id, string Name);

    /// <summary>Source-generated serializer, as an app writes it (trim-safe).</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(DirectFile))]
    internal sealed partial class DirectJsonContext : JsonSerializerContext;

    /// <summary>
    /// Stands for the browser's <c>fetch</c>: records each request and its body, answers with
    /// <c>answer</c>, or waits for cancellation when <see cref="Hang"/> is set.
    /// </summary>
    internal sealed class FakeBrowser(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public bool Hang { get; init; }

        public bool SawCancellation { get; private set; }

        public Task Started => _started.Task;

        public static HttpResponseMessage Text(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
        {
            HttpResponseMessage response = new(status) { Content = new StringContent(body) };
            foreach ((string name, string value) in headers)
            {
                if (!response.Headers.TryAddWithoutValidation(name, value))
                {
                    response.Content.Headers.Remove(name);
                    response.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            _started.TrySetResult();
            if (Hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SawCancellation = true;
                    throw;
                }
            }

            HttpResponseMessage response = answer(request);
            response.RequestMessage = request;
            return response;
        }
    }

    /// <summary>A body whose second part only arrives once <c>gate</c> completes.</summary>
    private sealed class TwoPartStream(byte[] first, byte[] second, Task gate) : Stream
    {
        private int _part;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            switch (_part++)
            {
                case 0:
                    first.CopyTo(buffer);
                    return first.Length;
                case 1:
                    await gate.WaitAsync(cancellationToken);
                    second.CopyTo(buffer);
                    return second.Length;
                default:
                    return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
