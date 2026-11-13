using System.Net;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>ADR 0266 §7: the OS's downloader reports the bytes of a package as they arrive.</summary>
public sealed class HttpAppPackageDownloaderTests
{
    private static readonly Uri Package = new("https://raw.githubusercontent.com/Kazo-ca/Os-App-Sample/kazoos-packages/sample-1.0.0.kazoapp");

    [Fact]
    public async Task A_download_reports_zero_then_growing_counts_up_to_the_announced_size()
    {
        byte[] body = new byte[200_000];
        Random.Shared.NextBytes(body);
        HttpAppPackageDownloader downloader = new(new HttpClient(new Answer(body, announceLength: true)));
        List<AppPackageDownloadProgress> reports = [];

        byte[]? downloaded = await downloader.DownloadAsync(Package, 1_000_000, new Recorder(reports));

        Assert.Equal(body, downloaded);
        Assert.Equal(new AppPackageDownloadProgress(0, body.Length), reports[0]);
        Assert.Equal(new AppPackageDownloadProgress(body.Length, body.Length), reports[^1]);
        Assert.True(reports.Count > 2);
        Assert.Equal(reports.Select(report => report.BytesReceived).Order(), reports.Select(report => report.BytesReceived));
    }

    [Fact]
    public async Task A_host_without_a_content_length_reports_an_unknown_size()
    {
        byte[] body = new byte[1_000];
        HttpAppPackageDownloader downloader = new(new HttpClient(new Answer(body, announceLength: false)));
        List<AppPackageDownloadProgress> reports = [];

        await downloader.DownloadAsync(Package, 1_000_000, new Recorder(reports));

        Assert.All(reports, report => Assert.Null(report.TotalBytes));
        Assert.Equal(body.Length, reports[^1].BytesReceived);
    }

    [Fact]
    public async Task The_overload_without_progress_downloads_the_same_body()
    {
        byte[] body = [1, 2, 3];
        HttpAppPackageDownloader downloader = new(new HttpClient(new Answer(body, announceLength: true)));

        Assert.Equal(body, await downloader.DownloadAsync(Package, 10));
    }

    /// <summary>Answers every request with the body, announcing its length or not.</summary>
    private sealed class Answer(byte[] body, bool announceLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BodyContent(body, announceLength) });
    }

    /// <summary>A body whose length is hidden when the host sends none, as a chunked answer does.</summary>
    private sealed class BodyContent(byte[] body, bool announceLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(body));

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return announceLength;
        }
    }

    private sealed class Recorder(List<AppPackageDownloadProgress> reports) : IProgress<AppPackageDownloadProgress>
    {
        public void Report(AppPackageDownloadProgress value) => reports.Add(value);
    }
}
