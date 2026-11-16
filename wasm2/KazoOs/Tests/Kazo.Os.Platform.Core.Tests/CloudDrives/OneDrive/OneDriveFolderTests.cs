using System.Net;
using System.Text;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives;
using Kazo.Os.Platform.Core.CloudDrives.OneDrive;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.OneDrive;

/// <summary>
/// ADR 0267 §6: a OneDrive drive over Microsoft Graph, against a fake Graph. Items by path,
/// <c>eTag</c> revisions with <c>If-Match</c>, downloads by <c>@microsoft.graph.downloadUrl</c>
/// without the token, upload sessions for large files, quota, and Graph's refusals as the errors
/// the OS knows.
/// </summary>
public sealed class OneDriveFolderTests
{
    private const string Account = "alice";

    private readonly FakeCloudAccountTokens _tokens = new();
    private readonly FakeGraphServer _graph;

    public OneDriveFolderTests()
    {
        _tokens.Add(Account, CloudOAuthProvider.OneDriveId);
        _graph = new FakeGraphServer(() => _tokens.CurrentToken(Account));
    }

    [Fact]
    public async Task Listing_gives_files_and_folders_with_the_etag_as_revision_across_pages()
    {
        _graph.PageSize = 2;
        FakeGraphServer.Node report = _graph.Put("hello", "report.txt");
        _graph.Folder("Photos");
        _graph.Put("x", "b.txt");
        _graph.Root.Children.Add(new FakeGraphServer.Node("pkg", "Notes", isFolder: false) { IsPackage = true });
        OneDriveFolder folder = Folder();

        IReadOnlyList<HostFolderEntry> entries = await folder.ListAsync([]);

        Assert.Equal(["Photos", "b.txt", "report.txt"], entries.Select(static entry => entry.Name).Order(StringComparer.Ordinal));
        HostFolderEntry file = entries.Single(static entry => entry.Name == "report.txt");
        Assert.Equal(HostFolderEntryKind.File, file.Kind);
        Assert.Equal(5, file.Length);
        Assert.Equal(report.ETag, file.Revision);
        Assert.Equal(report.Modified, file.LastModifiedUtc);
        HostFolderEntry photos = entries.Single(static entry => entry.Name == "Photos");
        Assert.Equal(HostFolderEntryKind.Directory, photos.Kind);
        Assert.Null(photos.Revision);
        Assert.Equal(2, _graph.Requests.Count(static request => request.Uri.AbsolutePath.EndsWith("/children", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Reading_downloads_from_the_download_url_without_the_token()
    {
        _graph.Put("bonjour", "Docs", "a.txt");
        OneDriveFolder folder = Folder();

        byte[] content = await folder.ReadAsync(["Docs", "a.txt"]);

        Assert.Equal("bonjour", Encoding.UTF8.GetString(content));
        Assert.Collection(
            _graph.Requests,
            request =>
            {
                Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/root:/Docs/a.txt:", request.Uri.AbsoluteUri);
                Assert.Equal("Bearer token-alice-1", request.Authorization);
            },
            request =>
            {
                Assert.Equal(FakeGraphServer.DownloadHost, request.Uri.Host);
                Assert.Null(request.Authorization);
            });
        Assert.DoesNotContain(_graph.Requests, static request => request.Uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal));
        Assert.Equal(HostFolderErrorKind.TypeMismatch, (await Refused(() => folder.ReadAsync(["Docs"]))).Kind);
        // OneDrive finds "A.TXT" as "a.txt"; Kazo OS is case-sensitive.
        Assert.Equal(HostFolderErrorKind.NotFound, (await Refused(() => folder.ReadAsync(["Docs", "A.TXT"]))).Kind);
    }

    [Fact]
    public async Task The_app_folder_access_and_a_chosen_root_address_their_own_folder_with_escaped_names()
    {
        _graph.Put("in app", "Apps", "Kazo OS", "Work", "100% #1.txt");
        _graph.Put("outside", "Work", "100% #1.txt");
        OneDriveFolder folder = Folder(CloudAccessScope.AppFolder, "Work");

        Assert.Equal("alice@example.test", folder.Name);
        Assert.Equal("in app", Encoding.UTF8.GetString(await folder.ReadAsync(["100% #1.txt"])));
        Assert.Equal(
            "/v1.0/me/drive/special/approot:/Work/100%25%20%231.txt:",
            _graph.Requests[0].Uri.AbsolutePath);
        Assert.Equal(HostFolderEntryKind.Directory, (await folder.StatAsync([])).Kind);
    }

    [Fact]
    public async Task Creating_refuses_a_taken_name_whatever_its_case_and_names_onedrive_refuses()
    {
        _graph.Put("x", "Report.txt");
        OneDriveFolder folder = Folder();

        await folder.CreateAsync(["New"], HostFolderEntryKind.Directory);
        await folder.CreateAsync(["New", "empty.txt"], HostFolderEntryKind.File);

        Assert.True(_graph.Find("New")!.IsFolder);
        Assert.Empty(_graph.Find("New", "empty.txt")!.Content);
        Assert.Equal(HostFolderErrorKind.AlreadyExists, (await Refused(() => folder.CreateAsync(["report.TXT"], HostFolderEntryKind.File))).Kind);
        Assert.Equal(HostFolderErrorKind.AlreadyExists, (await Refused(() => folder.CreateAsync(["new"], HostFolderEntryKind.Directory))).Kind);
        int before = _graph.Requests.Count;
        foreach (string name in new[] { "a:b", "what?", "trailing.", " lead", "pipe|" })
        {
            Assert.Equal(HostFolderErrorKind.InvalidName, (await Refused(() => folder.CreateAsync([name], HostFolderEntryKind.File))).Kind);
        }

        Assert.Equal(before, _graph.Requests.Count);
        _graph.FailNext(HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(HostFolderErrorKind.InvalidName, (await Refused(() => folder.CreateAsync(["CON"], HostFolderEntryKind.Directory))).Kind);
    }

    [Fact]
    public async Task A_conditional_write_sends_the_etag_and_a_changed_file_is_a_revision_conflict()
    {
        FakeGraphServer.Node node = _graph.Put("v1", "a.txt");
        OneDriveFolder folder = Folder();
        string revision = (await folder.StatAsync(["a.txt"])).Revision!;

        await folder.WriteIfMatchAsync(["a.txt"], Encoding.UTF8.GetBytes("v2"), revision);
        HostFolderException conflict = await Refused(() => folder.WriteIfMatchAsync(["a.txt"], Encoding.UTF8.GetBytes("v3"), revision));
        await folder.WriteAsync(["a.txt"], Encoding.UTF8.GetBytes("v4"));

        Assert.Equal(HostFolderErrorKind.RevisionConflict, conflict.Kind);
        Assert.Equal("v4", node.Text);
        Assert.Equal(revision, _graph.Requests.First(static request => request.Method == HttpMethod.Put).IfMatch);
        Assert.Null(_graph.Requests.Last().IfMatch);
    }

    [Fact]
    public async Task A_large_file_goes_through_an_upload_session_in_pieces_without_the_token()
    {
        FakeGraphServer.Node node = _graph.Put("old", "big.bin");
        OneDriveFolder folder = Folder();
        byte[] content = new byte[OneDriveFolder.UploadChunkSize + OneDriveFolder.SimpleUploadLimit];
        new Random(7).NextBytes(content);

        await folder.WriteIfMatchAsync(["big.bin"], content, node.ETag);

        Assert.Equal(content, node.Content);
        FakeGraphServer.RecordedRequest session = Assert.Single(_graph.Requests, static request => request.Uri.AbsolutePath.EndsWith("/createUploadSession", StringComparison.Ordinal));
        Assert.Equal("Bearer token-alice-1", session.Authorization);
        Assert.NotNull(session.IfMatch);
        FakeGraphServer.RecordedRequest[] pieces = [.. _graph.Requests.Where(static request => request.Uri.Host == FakeGraphServer.UploadHost)];
        Assert.Equal(2, pieces.Length);
        Assert.All(pieces, static piece => Assert.Null(piece.Authorization));
        Assert.Equal($"bytes 0-{OneDriveFolder.UploadChunkSize - 1}/{content.Length}", pieces[0].ContentRange);

        string stale = (await folder.StatAsync(["big.bin"])).Revision!;
        node.Content = [1];
        node.ETag = "\"changed\"";
        Assert.Equal(HostFolderErrorKind.RevisionConflict, (await Refused(() => folder.WriteIfMatchAsync(["big.bin"], content, stale))).Kind);
        Assert.Equal(0, _graph.OpenSessions);
    }

    [Fact]
    public async Task A_file_over_128_mib_is_listed_but_not_read()
    {
        _graph.Put("small", "huge.iso").ReportedSize = OneDriveFolder.MaxFileBytes + 1;
        OneDriveFolder folder = Folder();

        HostFolderEntry entry = Assert.Single(await folder.ListAsync([]));

        Assert.Equal(OneDriveFolder.MaxFileBytes + 1, entry.Length);
        Assert.Equal(HostFolderErrorKind.FileTooLarge, (await Refused(() => folder.ReadAsync(["huge.iso"]))).Kind);
        Assert.DoesNotContain(_graph.Requests, static request => request.Uri.Host == FakeGraphServer.DownloadHost);
    }

    [Fact]
    public async Task Deleting_refuses_a_non_empty_folder_unless_recursive_and_never_the_root()
    {
        _graph.Put("x", "Full", "x.txt");
        _graph.Folder("Empty");
        OneDriveFolder folder = Folder();

        Assert.Equal(HostFolderErrorKind.NotEmpty, (await Refused(() => folder.DeleteAsync(["Full"], recursive: false))).Kind);
        await folder.DeleteAsync(["Empty"], recursive: false);
        await folder.DeleteAsync(["Full"], recursive: true);
        Assert.Equal(HostFolderErrorKind.NotAllowed, (await Refused(() => folder.DeleteAsync([], recursive: true))).Kind);
        Assert.Equal(HostFolderErrorKind.NotFound, (await Refused(() => folder.DeleteAsync(["Empty"], recursive: true))).Kind);

        Assert.Empty(_graph.Root.Children);
    }

    [Fact]
    public async Task Moving_renames_in_place_or_moves_to_another_folder_in_one_request()
    {
        FakeGraphServer.Node file = _graph.Put("x", "a.txt");
        FakeGraphServer.Node archive = _graph.Folder("Archive");
        _graph.Put("y", "Archive", "taken.txt");
        OneDriveFolder folder = Folder();

        Assert.True(await folder.TryMoveAsync(["a.txt"], ["b.txt"]));
        Assert.Equal("b.txt", file.Name);
        Assert.True(await folder.TryMoveAsync(["b.txt"], ["Archive", "c.txt"]));
        Assert.Same(archive, file.Parent);
        Assert.Equal("c.txt", file.Name);
        Assert.Equal(HostFolderErrorKind.AlreadyExists, (await Refused(() => folder.TryMoveAsync(["Archive", "c.txt"], ["Archive", "TAKEN.txt"]))).Kind);
        Assert.Equal(HostFolderErrorKind.TypeMismatch, (await Refused(() => folder.TryMoveAsync(["Archive", "c.txt"], ["Archive", "taken.txt", "d.txt"]))).Kind);
    }

    [Fact]
    public async Task The_quota_comes_from_the_drive_and_is_absent_when_the_access_cannot_read_it()
    {
        _graph.QuotaTotal = 1000;
        _graph.Put("12345", "a.txt");
        OneDriveFolder folder = Folder();

        Assert.Equal(new HostFolderQuota(1000, 995), await folder.GetQuotaAsync());
        _graph.QuotaForbidden = true;
        Assert.Null(await folder.GetQuotaAsync());
    }

    [Fact]
    public async Task Graph_refusals_become_the_errors_the_os_knows()
    {
        _graph.Put("x", "a.txt");
        OneDriveFolder folder = Folder();

        Assert.Equal(HostFolderErrorKind.NotFound, (await Refused(() => folder.StatAsync(["missing"]))).Kind);
        _graph.FailNext(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HostFolderErrorKind.Unavailable, (await Refused(() => folder.StatAsync(["a.txt"]))).Kind);
        _graph.FailNext(HttpStatusCode.InternalServerError);
        Assert.Equal(HostFolderErrorKind.Unavailable, (await Refused(() => folder.StatAsync(["a.txt"]))).Kind);
        _graph.FailNext(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(3));
        Assert.Equal(HostFolderErrorKind.Failure, (await Refused(() => folder.StatAsync(["a.txt"]))).Kind);
        _graph.FailNext(HttpStatusCode.Forbidden, "quotaLimitReached");
        Assert.Equal(HostFolderErrorKind.NoSpace, (await Refused(() => folder.WriteAsync(["a.txt"], [1]))).Kind);
        _graph.FailNext(HttpStatusCode.InsufficientStorage);
        Assert.Equal(HostFolderErrorKind.NoSpace, (await Refused(() => folder.WriteAsync(["a.txt"], [1]))).Kind);
        _graph.FailNext(HttpStatusCode.Forbidden, "accessDenied");
        Assert.Equal(HostFolderErrorKind.NotAllowed, (await Refused(() => folder.StatAsync(["a.txt"]))).Kind);
        _graph.Online = false;
        Assert.Equal(HostFolderErrorKind.Unavailable, (await Refused(() => folder.ListAsync([]))).Kind);
    }

    private OneDriveFolder Folder(CloudAccessScope scope = CloudAccessScope.Full, params string[] root) =>
        new(new CloudDriveConnection(_tokens, Account, new HttpClient(_graph)), new HttpClient(_graph), "alice@example.test", scope, root);

    private static async Task<HostFolderException> Refused(Func<ValueTask> action) =>
        await Assert.ThrowsAsync<HostFolderException>(() => action().AsTask());

    private static async Task<HostFolderException> Refused<T>(Func<ValueTask<T>> action) =>
        await Assert.ThrowsAsync<HostFolderException>(() => action().AsTask());
}
