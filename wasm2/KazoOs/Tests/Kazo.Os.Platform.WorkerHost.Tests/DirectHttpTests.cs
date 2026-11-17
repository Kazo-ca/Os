using System.Net;
using System.Net.Http;
using System.Text.Json;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.WorkerHost.Protocol;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0282 §4: <see cref="KazoDirectHttpClient"/> works the same on the main thread and in a Web
/// Worker. In a worker the OS checks the capability and the declared host, then the worker sends
/// with its own <c>fetch</c>. The "browser" is a fake handler: no test reaches the Internet.
/// </summary>
public sealed class DirectHttpTests : IDisposable
{
    private readonly HttpMessageInvoker _previous = KazoDirectHttpTransport.Shared;
    private readonly RecordingBrowser _browser = new();

    public DirectHttpTests() => KazoDirectHttpTransport.Shared = new HttpMessageInvoker(_browser);

    public void Dispose() => KazoDirectHttpTransport.Shared = _previous;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_declared_host_is_reached_from_the_main_thread_and_from_a_worker(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: workers);

        ShellRun run = await os.RunAsync("direct https://api.example.com/hello").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("200 hello from api.example.com", run.Stdout.Trim());
        Assert.Equal("https://api.example.com/hello", Assert.Single(_browser.Uris));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_undeclared_host_is_refused_by_the_OS_and_nothing_leaves(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: workers);

        ShellRun run = await os.RunAsync("direct https://other.example.com/hello").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, run.ExitCode);
        Assert.Matches("^(Kazo)?UnauthorizedAccessException: ", run.Stderr.Trim());
        Assert.Contains("other.example.com", run.Stderr);
        Assert.Empty(_browser.Uris);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_capability_the_OS_refuses_and_nothing_leaves(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: workers);

        ShellRun run = await os.RunAsync("nodirect https://api.example.com/hello").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, run.ExitCode);
        Assert.Matches("^(Kazo)?UnauthorizedAccessException: ", run.Stderr.Trim());
        Assert.Contains("network.direct.access", run.Stderr);
        Assert.Empty(_browser.Uris);
    }

    [Fact]
    public void The_check_carries_method_address_headers_and_body_size_but_not_the_body()
    {
        KazoDirectHttpRequest request = new(HttpMethod.Put, new Uri("https://api.example.com/upload?x=1"))
        {
            Content = KazoDirectHttpContent.FromBytes(new byte[] { 1, 2, 3 }, "application/zip")
        };
        request.Headers.Add(new("Authorization", "Bearer t"));

        KazoDirectHttpRequest read = RoundTrip(request);

        Assert.Equal(HttpMethod.Put, read.Method);
        Assert.Equal(request.RequestUri, read.RequestUri);
        Assert.Equal(request.Headers, read.Headers);
        Assert.Equal("application/zip", read.Content!.ContentType);
        Assert.Equal(3, read.Content.Length);
    }

    private static KazoDirectHttpRequest RoundTrip(KazoDirectHttpRequest request)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WireCodec.WriteDirectCheck(writer, request);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return WireCodec.ReadDirectCheck(document.RootElement);
    }

    /// <summary>Stands for the browser's <c>fetch</c>: answers every request with its host.</summary>
    private sealed class RecordingBrowser : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<string> _uris = [];

        public IReadOnlyList<string> Uris
        {
            get
            {
                lock (_gate)
                {
                    return [.. _uris];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _uris.Add(request.RequestUri!.AbsoluteUri);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent($"hello from {request.RequestUri.Host}")
            });
        }
    }
}
