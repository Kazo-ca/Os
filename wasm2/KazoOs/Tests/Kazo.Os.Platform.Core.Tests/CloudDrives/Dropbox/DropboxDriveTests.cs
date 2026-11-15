using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives.Dropbox;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using static Kazo.Os.Platform.Core.Tests.CloudDrives.Dropbox.DropboxContractTests;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.Dropbox;

/// <summary>
/// What is Dropbox's own (ADR 0267 §6 and §8): case-insensitive names, the request formats the API
/// asks for, a drive that starts in a folder, paged listings, the source, and a drive mounted
/// under <c>/mnt/Dropbox</c> by the network drive service.
/// </summary>
public sealed class DropboxDriveTests
{
    private readonly FakeCloudAccountTokens _tokens = new();

    [Fact]
    public async Task Names_differing_only_by_case_are_one_entry_under_its_real_name()
    {
        DropboxContractService service = new(_tokens);
        service.SeedFile("/README.txt", Encoding.UTF8.GetBytes("x"));
        IHostFolder folder = await service.OpenAsync();

        HostFolderException missing = await Assert.ThrowsAsync<HostFolderException>(async () => await folder.StatAsync(["readme.txt"]));
        HostFolderException taken = await Assert.ThrowsAsync<HostFolderException>(async () => await folder.CreateAsync(["readme.txt"], HostFolderEntryKind.File));

        Assert.Equal(HostFolderErrorKind.NotFound, missing.Kind);
        Assert.Equal(HostFolderErrorKind.AlreadyExists, taken.Kind);
        Assert.Equal(["README.txt"], (await folder.ListAsync([])).Select(static entry => entry.Name));
    }

    [Fact]
    public async Task A_rename_that_changes_only_the_case_goes_through_a_temporary_name()
    {
        DropboxContractService service = new(_tokens);
        service.SeedFile("/README.txt", Encoding.UTF8.GetBytes("x"));
        IHostFolder folder = await service.OpenAsync();

        Assert.True(await folder.TryMoveAsync(["README.txt"], ["Readme.txt"]));

        Assert.Equal("/Readme.txt", service.Server.DisplayPath("/readme.txt"));
        Assert.Equal(["Readme.txt"], (await folder.ListAsync([])).Select(static entry => entry.Name));
        Assert.Equal(2, service.Server.Requests.Count(static request => request.Endpoint == "files/move_v2"));
    }

    [Fact]
    public async Task Requests_use_the_formats_dropbox_asks_for()
    {
        DropboxContractService service = new(_tokens);
        service.SeedFile("/Été/a.txt", Encoding.UTF8.GetBytes("v1"));
        IHostFolder folder = await service.OpenAsync();
        string revision = (await folder.StatAsync(["Été", "a.txt"])).Revision!;

        await ((IConditionalHostFolder)folder).WriteIfMatchAsync(["Été", "a.txt"], Encoding.UTF8.GetBytes("v2"), revision);
        await folder.CreateAsync(["Été", "b.txt"], HostFolderEntryKind.File);

        var metadata = service.Server.Requests.First(static request => request.Endpoint == "files/get_metadata");
        Assert.Equal("application/json", metadata.ContentType);
        Assert.Equal($"Bearer {_tokens.CurrentToken(service.AccountId)}", metadata.Authorization);
        Assert.Equal("/Été/a.txt", metadata.Argument!["path"]!.GetValue<string>());
        var uploads = service.Server.Requests.Where(static request => request.Endpoint == "files/upload").ToList();
        Assert.Equal("update", uploads[0].Argument!["mode"]![".tag"]!.GetValue<string>());
        Assert.Equal(revision, uploads[0].Argument!["mode"]!["update"]!.GetValue<string>());
        Assert.False(uploads[0].Argument!["autorename"]!.GetValue<bool>());
        Assert.True(uploads[0].Argument!["strict_conflict"]!.GetValue<bool>());
        Assert.Equal("add", uploads[1].Argument!["mode"]!.GetValue<string>());
        Assert.Equal("v2", Encoding.UTF8.GetString(service.Server.Read("/Été/a.txt")!));
    }

    [Fact]
    public async Task A_long_folder_is_read_page_by_page()
    {
        DropboxContractService service = new(_tokens);
        for (int index = 0; index < 5; index++)
        {
            service.SeedFile($"/f{index}.txt", [1]);
        }

        service.Server.PageSize = 2;
        IHostFolder folder = await service.OpenAsync();

        Assert.Equal(5, (await folder.ListAsync([])).Count);
        Assert.Equal(2, service.Server.Requests.Count(static request => request.Endpoint == "files/list_folder/continue"));
    }

    [Fact]
    public async Task A_drive_can_start_in_a_folder_of_the_account()
    {
        DropboxContractService service = new(_tokens);
        service.SeedFile("/Work/Docs/a.txt", Encoding.UTF8.GetBytes("inside"));
        service.SeedFile("/outside.txt", Encoding.UTF8.GetBytes("outside"));

        NetworkDriveCreation creation = (await service.Source.CreateAsync(new NetworkDriveRequest(service.AccountId, "Work/Docs/")))!;

        Assert.Equal("/Work/Docs", creation.Reference.Root);
        Assert.Equal(["a.txt"], (await creation.Folder.ListAsync([])).Select(static entry => entry.Name));
        Assert.Equal("inside", Encoding.UTF8.GetString(await creation.Folder.ReadAsync(["a.txt"])));
        Assert.Equal(HostFolderEntryKind.Directory, (await creation.Folder.StatAsync([])).Kind);
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.Source.CreateAsync(new NetworkDriveRequest(service.AccountId, "Work/../x")));
        Assert.Null((await service.Source.CreateAsync(new NetworkDriveRequest(service.AccountId, "/")))!.Reference.Root);
    }

    [Fact]
    public async Task The_source_mounts_only_a_connected_dropbox_account_and_never_opens_a_window()
    {
        DropboxContractService service = new(_tokens);
        _tokens.Add("od-bob", CloudOAuthProvider.OneDriveId);
        DropboxDriveSource source = service.Source;
        int availability = 0;
        ((INetworkDriveSource)source).AvailabilityChanged += () => availability++;

        Assert.True(await source.IsAvailableAsync());
        Assert.False(await new DropboxDriveSource(_tokens, new StaticCatalog(configured: false), new HttpClient()).IsAvailableAsync());
        await Assert.ThrowsAsync<ArgumentException>(async () => await source.CreateAsync(new NetworkDriveRequest()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await source.CreateAsync(new NetworkDriveRequest("nobody")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await source.CreateAsync(new NetworkDriveRequest("od-bob")));
        NetworkDriveCreation creation = (await source.CreateAsync(new NetworkDriveRequest(service.AccountId)))!;
        Assert.Equal(new NetworkDriveReference("alice@example.test", Account: service.AccountId), creation.Reference);
        Assert.Equal("Dropbox", creation.DefaultMountName);
        Assert.Equal(0, _tokens.Reconnections);
        Assert.Empty(service.Server.Requests);

        Assert.NotNull(await source.OpenAsync(creation.Reference));
        _tokens.Remove(service.AccountId);
        Assert.Null(await source.OpenAsync(creation.Reference));
        Assert.Equal(1, availability);
    }

    [Fact]
    public async Task An_app_folder_account_mounts_the_same_way()
    {
        DropboxContractService service = new(_tokens, CloudAccessScope.AppFolder);
        service.SeedFile("/kept.txt", [42]);

        IHostFolder folder = await service.OpenAsync();

        Assert.Equal([42], await folder.ReadAsync(["kept.txt"]));
    }

    [Fact]
    public async Task Mounted_by_the_network_drive_service_it_shows_its_type_quota_and_refuses_a_stale_write()
    {
        DropboxContractService service = new(_tokens);
        service.SeedFile("/a.txt", Encoding.UTF8.GetBytes("v1"));
        Fixture fixture = await Fixture.CreateAsync(service.Source);

        NetworkDrive? drive = await fixture.Drives.MountAsync("dropbox", null, new NetworkDriveRequest(service.AccountId));

        Assert.Equal("/mnt/Dropbox", drive!.MountPoint.Value);
        Assert.Equal("dropbox", drive.Type);
        Assert.Contains("dropbox:alice@example.test /mnt/Dropbox dropbox rw,nosuid,nodev 0 0",
            await fixture.Files.ReadTextAsync("/proc/mounts", fixture.Context), StringComparison.Ordinal);
        Assert.Equal(new HostFolderQuota(service.Server.Allocated, service.Server.Allocated - service.Server.Used),
            await fixture.Drives.GetQuotaAsync(drive.MountPoint));
        Assert.Equal("v1", await fixture.Files.ReadTextAsync("/mnt/Dropbox/a.txt", fixture.Context));

        VirtualPath path = VirtualPath.Parse("/mnt/Dropbox/a.txt");
        FileSystemResult<FileSystemEntrySnapshot> stat = await fixture.Files.Service.StatAsync(new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), fixture.Context);
        service.SeedFile("/a.txt", Encoding.UTF8.GetBytes("changed elsewhere"));
        FileSystemMutationResult stale = await fixture.Files.Service.WriteAsync(
            new FileSystemWriteRequest(path, stat.Value!.Metadata.Revision), new BytesSource(Encoding.UTF8.GetBytes("mine")), fixture.Context);

        Assert.False(stale.Succeeded);
        Assert.Equal(FileSystemErrorCode.RevisionConflict, stale.Transaction.Error!.Code);
        Assert.Equal("changed elsewhere", Encoding.UTF8.GetString(service.Server.Read("/a.txt")!));
    }

    [Fact]
    public async Task Offline_the_drive_disconnects_and_reconnects_when_the_page_says_the_network_is_back()
    {
        DropboxContractService service = new(_tokens);
        Fixture fixture = await Fixture.CreateAsync(service.Source);
        await fixture.Drives.MountAsync("dropbox", null, new NetworkDriveRequest(service.AccountId));
        service.Online = false;

        FileSystemResult<FileSystemDirectorySnapshot> listed = await fixture.Files.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse("/mnt/Dropbox")), fixture.Context);
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, listed.Error!.Code);
        Assert.False(Assert.Single(fixture.Drives.GetDrives()).IsConnected);

        service.Online = true;
        await fixture.Drives.ReconnectAvailableAsync();

        Assert.True(Assert.Single(fixture.Drives.GetDrives()).IsConnected);
    }

    private sealed class Fixture
    {
        private Fixture(Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture files, INetworkDriveSource source)
        {
            Files = files;
            AuthenticatedPrincipal principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
            Drives = new NetworkDriveService(files.Router, [new HostFolderDriveSource(new FakeHostFolderAccess()), source], () => principal);
            Context = files.AppContext(null, AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite);
        }

        public Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture Files { get; }

        public NetworkDriveService Drives { get; }

        public FileSystemAuthorizationContext Context { get; }

        public static async Task<Fixture> CreateAsync(INetworkDriveSource source) => new(await CreateSeededAsync(), source);
    }
}
