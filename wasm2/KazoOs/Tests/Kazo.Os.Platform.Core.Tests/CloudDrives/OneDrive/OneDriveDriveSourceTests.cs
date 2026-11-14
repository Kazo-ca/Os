using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives.OneDrive;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.OneDrive;

/// <summary>
/// ADR 0267 §3 and §6: the <c>onedrive</c> source mounts a connected account's OneDrive through the
/// network drive service, opens no window, reads only the local account list at login, and
/// reconnects its drives when the account signs in again.
/// </summary>
public sealed class OneDriveDriveSourceTests
{
    private const string Account = "od-alice";

    private readonly FakeCloudAccountTokens _tokens = new();
    private readonly FakeGraphServer _graph;

    public OneDriveDriveSourceTests()
    {
        _tokens.Add(Account, CloudOAuthProvider.OneDriveId, CloudAccessScope.Full, "alice@example.test");
        _graph = new FakeGraphServer(() => _tokens.CurrentToken(Account));
    }

    [Fact]
    public async Task The_source_is_offered_only_when_onedrive_has_an_app_id()
    {
        Assert.True(await Source().IsAvailableAsync());
        Assert.False(await Source(configured: false).IsAvailableAsync());
        Assert.Equal("onedrive", Source().Type);
        Assert.Equal("OneDrive", Source().DefaultMountName);
    }

    [Fact]
    public async Task Creating_needs_a_connected_onedrive_account_and_opens_no_window()
    {
        _tokens.Add("dbx", CloudOAuthProvider.DropboxId);
        OneDriveDriveSource source = Source();

        await Assert.ThrowsAsync<ArgumentException>(() => source.CreateAsync(new NetworkDriveRequest()).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => source.CreateAsync(new NetworkDriveRequest("nobody")).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => source.CreateAsync(new NetworkDriveRequest("dbx")).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => source.CreateAsync(new NetworkDriveRequest(Account, "Work/../..")).AsTask());
        NetworkDriveCreation creation = (await source.CreateAsync(new NetworkDriveRequest(Account, @"\Work\2026/")))!;

        Assert.Equal(new NetworkDriveReference("alice@example.test", Account: Account, Root: "Work/2026"), creation.Reference);
        Assert.Equal("OneDrive", creation.DefaultMountName);
        Assert.Equal("alice@example.test", creation.Folder.Name);
        Assert.Equal(0, _tokens.Reconnections);
        Assert.Empty(_graph.Requests);
    }

    [Fact]
    public async Task Opening_at_login_reaches_no_service_and_drops_a_removed_account()
    {
        OneDriveDriveSource source = Source();
        NetworkDriveReference reference = new("alice@example.test", Account: Account);

        Assert.NotNull(await source.OpenAsync(reference));
        Assert.Empty(_graph.Requests);
        _tokens.Remove(Account);
        Assert.Null(await source.OpenAsync(reference));
    }

    [Fact]
    public async Task A_onedrive_drive_mounts_reads_writes_and_reports_its_quota_through_the_os()
    {
        _graph.QuotaTotal = 1000;
        _graph.Put("from OneDrive", "Docs", "a.txt");
        Harness harness = await Harness.CreateAsync(Source());

        NetworkDrive drive = (await harness.Drives.MountAsync("onedrive", null, new NetworkDriveRequest(Account)))!;

        Assert.Equal("/mnt/OneDrive", drive.MountPoint.Value);
        Assert.Equal("onedrive", drive.Type);
        Assert.True(drive.IsConnected);
        Assert.Equal("from OneDrive", await harness.Fixture.ReadTextAsync("/mnt/OneDrive/Docs/a.txt", harness.Context));
        FileSystemMutationResult written = await harness.Fixture.Service.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse("/mnt/OneDrive/Docs/a.txt")),
            new BytesSource(Encoding.UTF8.GetBytes("written by Kazo OS")),
            harness.Context);
        Assert.True(written.Succeeded, $"write: {written.Transaction.Error?.Code}");
        Assert.Equal("written by Kazo OS", _graph.Find("Docs", "a.txt")!.Text);
        Assert.Equal(new HostFolderQuota(1000, 1000 - _graph.Used), await harness.Drives.GetQuotaAsync(drive.MountPoint));
        string mounts = await harness.Fixture.ReadTextAsync("/proc/mounts", harness.Context);
        Assert.Contains("onedrive:alice@example.test /mnt/OneDrive onedrive rw,nosuid,nodev 0 0", mounts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_24_hours_the_drive_waits_for_reconnect_and_comes_back_when_the_account_signs_in_again()
    {
        _graph.Put("x", "a.txt");
        Harness harness = await Harness.CreateAsync(Source());
        NetworkDrive drive = (await harness.Drives.MountAsync("onedrive", null, new NetworkDriveRequest(Account)))!;

        _tokens.RequireReconnect(Account);
        FileSystemResult<FileSystemContentReadHandle> failed = await harness.Fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/OneDrive/a.txt")), harness.Context);
        Assert.False(failed.Succeeded);
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, failed.Error!.Code);
        Assert.False(harness.Drives.GetDrives().Single().IsConnected);

        // "Reconnect" in the UI: the account signs in again (CloudAccountService raises Changed),
        // and the drive reconnects with no other gesture.
        await _tokens.ReconnectAsync(Account);
        await harness.Drives.ReconnectAvailableAsync();

        Assert.True(harness.Drives.GetDrives().Single().IsConnected);
        Assert.Equal("x", await harness.Fixture.ReadTextAsync(drive.MountPoint.Value + "/a.txt", harness.Context));
    }

    private OneDriveDriveSource Source(bool configured = true) =>
        new(_tokens, new OneDriveContractTests.StaticCatalog(configured), new HttpClient(_graph));

    private sealed class Harness
    {
        private Harness(VirtualFileSystemFixture fixture, OneDriveDriveSource source)
        {
            Fixture = fixture;
            AuthenticatedPrincipal principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
            Drives = new NetworkDriveService(fixture.Router, [new HostFolderDriveSource(new FakeHostFolderAccess()), source], () => principal);
            Context = fixture.AppContext(null, AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite);
        }

        public VirtualFileSystemFixture Fixture { get; }

        public NetworkDriveService Drives { get; }

        public FileSystemAuthorizationContext Context { get; }

        public static async Task<Harness> CreateAsync(OneDriveDriveSource source) => new(await CreateSeededAsync(), source);
    }
}
