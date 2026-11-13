using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;

/// <summary>
/// Drive sources (ADR 0267 §3, §4 and §9): the network drive service mounts what any source
/// creates, keeps its type in the drive list, reports quotas and service revisions, and reconnects
/// a drive by itself when its source comes back. The cloud source is the in-memory fake.
/// </summary>
public sealed class NetworkDriveSourceTests
{
    private const string Read = AppCapabilities.FileSystemSystemRead;
    private const string Write = AppCapabilities.FileSystemSystemWrite;
    private const string Account = "alice@example.test";

    [Fact]
    public async Task A_cloud_drive_mounts_at_the_source_default_name_with_its_type_and_account()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Cloud.Account(Account).Put("from the cloud", "Docs", "a.txt");

        NetworkDrive? drive = await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));
        NetworkDrive? second = await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account, "Docs"));

        Assert.NotNull(drive);
        Assert.Equal("/mnt/FakeCloud", drive.MountPoint.Value);
        Assert.Equal("fakecloud", drive.Type);
        Assert.Equal(Account, drive.Name);
        Assert.True(drive.IsConnected);
        Assert.Equal("/mnt/FakeCloud-2", second!.MountPoint.Value);
        Assert.Equal("from the cloud", await harness.Fixture.ReadTextAsync("/mnt/FakeCloud/Docs/a.txt", harness.Context));
        Assert.Equal("from the cloud", await harness.Fixture.ReadTextAsync("/mnt/FakeCloud-2/a.txt", harness.Context));

        string mounts = await harness.Fixture.ReadTextAsync("/proc/mounts", harness.Context);
        Assert.Contains($"fakecloud:{Account} /mnt/FakeCloud fakecloud rw,nosuid,nodev 0 0", mounts, StringComparison.Ordinal);

        using JsonDocument table = JsonDocument.Parse(await harness.TableAsync());
        JsonElement[] lines = [.. table.RootElement.GetProperty("drives").EnumerateArray()];
        Assert.Equal("fakecloud", lines[0].GetProperty("type").GetString());
        Assert.Equal(Account, lines[0].GetProperty("account").GetString());
        Assert.False(lines[0].TryGetProperty("root", out _));
        Assert.False(lines[0].TryGetProperty("key", out _));
        Assert.Equal("Docs", lines[1].GetProperty("root").GetString());
    }

    [Fact]
    public async Task Unknown_types_unavailable_sources_and_refused_requests_are_reported()
    {
        Harness harness = await Harness.CreateAsync();

        Assert.Equal(["fakecloud", "hostfs"], harness.Drives.SourceTypes);
        Assert.Equal(NetworkDriveError.NotSupported, (await Refused(() => harness.Drives.MountAsync("ftp", null, null))).Error);
        Assert.Equal(NetworkDriveError.InvalidRequest,
            (await Refused(() => harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest("nobody")))).Error);
        Assert.Null(await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest()));
        harness.Cloud.Available = false;
        Assert.False(await harness.Drives.IsAvailableAsync("fakecloud"));
        Assert.Equal(NetworkDriveError.NotSupported,
            (await Refused(() => harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account)))).Error);
        Assert.Empty(harness.Drives.GetDrives());
    }

    [Fact]
    public async Task Restore_reopens_each_drive_through_its_source_and_unmount_forgets_it_there()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Cloud.Account(Account).Put("x", "x.txt");
        harness.Access.NextPick = new FakeHostFolder("Projects");
        await harness.Drives.MountAsync(null);
        await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));
        await harness.Drives.UnmountAllAsync();

        await harness.Drives.RestoreAsync();

        Assert.Equal(
            [("/mnt/FakeCloud", "fakecloud"), ("/mnt/Projects", "hostfs")],
            harness.Drives.GetDrives().Select(static drive => (drive.MountPoint.Value, drive.Type)));
        Assert.Equal("x", await harness.Fixture.ReadTextAsync("/mnt/FakeCloud/x.txt", harness.Context));

        await harness.Drives.UnmountAsync("/mnt/FakeCloud");

        NetworkDriveReference forgotten = Assert.Single(harness.Cloud.Forgotten);
        Assert.Equal(Account, forgotten.Account);
        Assert.True(harness.Cloud.Opened[^1].Disposed);
        Assert.Single(harness.Access.StoredKeys);
    }

    [Fact]
    public async Task A_list_written_before_drive_sources_is_read_as_folders_of_the_computer()
    {
        Harness harness = await Harness.CreateAsync();
        FakeHostFolder folder = new("Old");
        folder.Put("kept", "a.txt");
        harness.Access.NextPick = folder;
        await harness.Drives.MountAsync(null);
        await harness.Drives.UnmountAllAsync();
        string key = Assert.Single(harness.Access.StoredKeys);
        string legacy = $$"""
            {
              "schemaVersion": 1,
              "drives": [
                { "mountPoint": "/mnt/Old", "name": "Old", "key": "{{key}}", "createdMountPoint": true },
                { "mountPoint": "/mnt/Gone", "name": "Gone", "key": "x", "createdMountPoint": true, "type": "nosuchtype" }
              ]
            }
            """;
        await harness.WriteTableAsync(legacy);

        await harness.Drives.RestoreAsync();

        NetworkDrive drive = Assert.Single(harness.Drives.GetDrives());
        Assert.Equal(("/mnt/Old", "hostfs", true), (drive.MountPoint.Value, drive.Type, drive.IsConnected));
        Assert.Equal("kept", await harness.Fixture.ReadTextAsync("/mnt/Old/a.txt", harness.Context));
        string table = await harness.TableAsync();
        Assert.DoesNotContain("/mnt/Gone", table, StringComparison.Ordinal);
        using JsonDocument written = JsonDocument.Parse(table);
        Assert.False(written.RootElement.GetProperty("drives")[0].TryGetProperty("type", out _));
    }

    [Fact]
    public async Task An_unreachable_service_mounts_disconnected_at_login_and_reconnects_by_itself()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Cloud.Account(Account).Put("back", "a.txt");
        await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));
        await harness.Drives.UnmountAllAsync();
        harness.Cloud.Online = false;
        int changes = 0;
        harness.Drives.Changed += () => changes++;

        await harness.Drives.RestoreAsync();

        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/FakeCloud"))!.IsConnected);
        FileSystemResult<FileSystemDirectorySnapshot> refused = await harness.Fixture.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse("/mnt/FakeCloud")), harness.Context);
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, refused.Error!.Code);
        changes = 0;

        harness.Cloud.Online = true;

        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/FakeCloud"))!.IsConnected);
        Assert.Equal(1, changes);
        Assert.Equal("back", await harness.Fixture.ReadTextAsync("/mnt/FakeCloud/a.txt", harness.Context));
    }

    [Fact]
    public async Task Losing_the_network_disconnects_the_drive_and_its_return_reconnects_it()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Cloud.Account(Account).Put("a", "a.txt");
        await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));

        harness.Cloud.Online = false;
        FileSystemResult<FileSystemContentReadHandle> offline = await harness.Fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/FakeCloud/a.txt")), harness.Context);

        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, offline.Error!.Code);
        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/FakeCloud"))!.IsConnected);

        harness.Cloud.Online = true;

        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/FakeCloud"))!.IsConnected);
    }

    [Fact]
    public async Task An_expired_sign_in_waits_for_a_reconnection_and_does_not_come_back_alone()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Cloud.Account(Account).Put("a", "a.txt");
        await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));
        harness.Cloud.Expire(Account);
        await harness.Fixture.Service.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse("/mnt/FakeCloud/a.txt")), harness.Context);

        await harness.Drives.ReconnectAvailableAsync();

        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/FakeCloud"))!.IsConnected);
        Assert.True(await harness.Drives.ReconnectAsync("/mnt/FakeCloud"));
        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/FakeCloud"))!.IsConnected);
    }

    [Fact]
    public async Task The_service_revision_guards_writes_and_its_refusal_is_a_revision_conflict()
    {
        Harness harness = await Harness.CreateAsync();
        FakeRemoteStorage storage = harness.Cloud.Account(Account);
        storage.Put("v1", "a.txt");
        await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));
        FakeRemoteFolder folder = harness.Cloud.Opened[^1];
        VirtualPath path = VirtualPath.Parse("/mnt/FakeCloud/a.txt");

        long first = await harness.RevisionAsync(path);
        storage.Put("changed on another device", "a.txt");
        long second = await harness.RevisionAsync(path);
        Assert.NotEqual(first, second);

        FileSystemMutationResult stale = await harness.WriteAsync(path, "mine", first);
        Assert.Equal(FileSystemErrorCode.RevisionConflict, stale.Transaction.Error!.Code);
        Assert.Equal(0, folder.ConditionalWrites);

        folder.BeforeNextConditionalWrite = () => storage.Put("raced", "a.txt");
        FileSystemMutationResult raced = await harness.WriteAsync(path, "mine", second);
        Assert.Equal(FileSystemErrorCode.RevisionConflict, raced.Transaction.Error!.Code);
        Assert.Equal("raced", storage.Text("a.txt"));
        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/FakeCloud"))!.IsConnected);

        FileSystemMutationResult written = await harness.WriteAsync(path, "mine", await harness.RevisionAsync(path));
        Assert.True(written.Succeeded, $"write: {written.Transaction.Error?.Code}");
        Assert.Equal("mine", storage.Text("a.txt"));
        Assert.Equal(2, folder.ConditionalWrites);
        Assert.Equal(await harness.RevisionAsync(path), written.Entry!.Metadata.Revision);
    }

    [Fact]
    public async Task The_quota_reaches_KazoDriveInfo_while_a_folder_of_the_computer_keeps_none()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Cloud.Account(Account, totalBytes: 10_000).Put(new string('x', 2_500), "big.txt");
        harness.Access.NextPick = new FakeHostFolder("Projects");
        await harness.Drives.MountAsync(null);
        await harness.Drives.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));
        AppDriveGateway gateway = new(() => harness.Fixture.Router, VirtualHardwareProfile.Default)
        {
            NetworkDrives = () => harness.Drives
        };

        IReadOnlyList<KazoDriveSnapshot> drives = await gateway.GetDrivesAsync();

        KazoDriveSnapshot cloud = drives.Single(static drive => drive.Name == "/mnt/FakeCloud");
        Assert.Equal(("fakecloud", Account, 10_000L, 7_500L), (cloud.DriveFormat, cloud.VolumeLabel, cloud.TotalSize, cloud.AvailableFreeSpace));
        Assert.Equal(KazoDriveType.Network, cloud.DriveType);
        KazoDriveSnapshot host = drives.Single(static drive => drive.Name == "/mnt/Projects");
        Assert.Equal(("hostfs", 0L, 0L), (host.DriveFormat, host.TotalSize, host.AvailableFreeSpace));

        harness.Cloud.Online = false;
        await harness.Fixture.Service.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse("/mnt/FakeCloud/big.txt")), harness.Context);
        KazoDriveSnapshot offline = (await gateway.GetDrivesAsync()).Single(static drive => drive.Name == "/mnt/FakeCloud");
        Assert.Equal((0L, false), (offline.TotalSize, offline.IsReady));
    }

    [Fact]
    public async Task The_folder_of_the_computer_keeps_its_time_based_revision_and_plain_writes()
    {
        Harness harness = await Harness.CreateAsync();
        FakeHostFolder folder = new("Projects");
        folder.Put("v1", "a.txt");
        harness.Access.NextPick = folder;
        await harness.Drives.MountAsync(null);
        VirtualPath path = VirtualPath.Parse("/mnt/Projects/a.txt");

        FileSystemMutationResult written = await harness.WriteAsync(path, "v2", await harness.RevisionAsync(path));

        Assert.True(written.Succeeded);
        Assert.Equal("v2", folder.Text("a.txt"));
        Assert.Null(await harness.Drives.GetQuotaAsync(VirtualPath.Parse("/mnt/Projects")));
        string mounts = await harness.Fixture.ReadTextAsync("/proc/mounts", harness.Context);
        Assert.Contains("host:Projects /mnt/Projects hostfs rw,nosuid,nodev 0 0", mounts, StringComparison.Ordinal);
    }

    private static Task<NetworkDriveException> Refused(Func<ValueTask<NetworkDrive?>> mount) =>
        Assert.ThrowsAsync<NetworkDriveException>(() => mount().AsTask());

    private sealed class Harness
    {
        private Harness(VirtualFileSystemFixture fixture)
        {
            Fixture = fixture;
            Principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
            Drives = new NetworkDriveService(fixture.Router, [new HostFolderDriveSource(Access), Cloud], () => Principal);
            Context = fixture.AppContext(null, Read, Write);
        }

        public VirtualFileSystemFixture Fixture { get; }

        public FakeHostFolderAccess Access { get; } = new();

        public FakeRemoteDriveSource Cloud { get; } = new();

        public AuthenticatedPrincipal? Principal { get; set; }

        public NetworkDriveService Drives { get; }

        public FileSystemAuthorizationContext Context { get; }

        private static string TablePath => "/home/alice/" + NetworkDriveService.TableRelativePath;

        public static async Task<Harness> CreateAsync() => new(await CreateSeededAsync());

        public Task<string> TableAsync() => Fixture.ReadTextAsync(TablePath, Fixture.SystemContext());

        public async Task WriteTableAsync(string json)
        {
            FileSystemMutationResult written = await Fixture.Service.WriteAsync(
                new FileSystemWriteRequest(VirtualPath.Parse(TablePath)), new BytesSource(Encoding.UTF8.GetBytes(json)), Fixture.SystemContext());
            Assert.True(written.Succeeded, $"table: {written.Transaction.Error?.Code}");
        }

        public async Task<long> RevisionAsync(VirtualPath path)
        {
            FileSystemResult<FileSystemEntrySnapshot> stat = await Fixture.Service.StatAsync(
                new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), Context);
            Assert.True(stat.Succeeded, $"stat: {stat.Error?.Code}");
            return stat.Value!.Metadata.Revision;
        }

        public ValueTask<FileSystemMutationResult> WriteAsync(VirtualPath path, string text, long expectedRevision) =>
            Fixture.Service.WriteAsync(
                new FileSystemWriteRequest(path, expectedRevision),
                new BytesSource(Encoding.UTF8.GetBytes(text)),
                Context);
    }
}
