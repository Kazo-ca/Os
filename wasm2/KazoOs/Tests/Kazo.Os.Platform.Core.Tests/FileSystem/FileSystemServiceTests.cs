using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class FileSystemServiceTests
{
    [Fact]
    public async Task Read_requires_exact_home_capability_even_when_mode_allows()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();

        FileSystemResult<FileSystemContentReadHandle> result = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/user/file.txt")),
            fixture.UserContext());

        Assert.Equal(FileSystemErrorCode.CapabilityDenied, result.Error?.Code);
    }

    [Fact]
    public async Task Read_with_capability_and_mode_returns_streamed_content()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();
        FileSystemAuthorizationContext context = fixture.UserContext(AppCapabilities.FileSystemUserHomeRead);

        FileSystemResult<FileSystemContentReadHandle> result = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/user/file.txt")),
            context);

        await using FileSystemContentReadHandle handle = result.Value!;
        Assert.Equal(7, handle.Content.ReadByte());
    }

    [Fact]
    public async Task User_cannot_write_system_path_with_capability_but_without_authority()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();
        FileSystemAuthorizationContext user = fixture.UserContext(AppCapabilities.FileSystemSystemWrite);
        FileSystemEntrySnapshot etc = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/etc")),
            fixture.SystemContext)).Value!;

        FileSystemMutationResult result = await fixture.Service.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse("/etc/user.conf"),
                FileSystemEntryKind.File,
                FileSystemPermissions.FromMode(0x01A4),
                etc.Metadata.Revision),
            user);

        Assert.Equal(FileSystemErrorCode.AuthorityDenied, result.Transaction.Error?.Code);
    }

    [Fact]
    public async Task Selected_handle_allows_scoped_read_without_broad_capability()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();
        FileSystemSelectedResourceHandle handle = new(
            Guid.Parse("d9428888-122b-11e1-b85c-61cd3cbb3210"),
            "org.kazoos.test",
            "user",
            VirtualPath.Parse("/home/user/file.txt"),
            FileSystemHandleAccess.Read,
            fixture.Now.AddMinutes(-1),
            fixture.Now.AddMinutes(1),
            1);

        FileSystemResult<FileSystemContentReadHandle> result = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/user/file.txt")),
            fixture.UserContext(handle: handle));

        Assert.True(result.Succeeded);
        await result.Value!.DisposeAsync();
    }

    [Fact]
    public async Task In_the_root_reserve_a_users_write_fails_with_no_space_and_asks_for_a_new_measure()
    {
        FullGuard guard = new();
        ServiceFixture fixture = await ServiceFixture.CreateAsync(guard);

        FileSystemMutationResult result = await fixture.WriteFileAsync(fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        Assert.Equal(FileSystemErrorCode.NoSpace, result.Transaction.Error?.Code);
        Assert.Equal(1, guard.Refreshes);
        // The reserve refused it, not the browser (ADR 0279): no refused write is reported.
        Assert.Equal(0, guard.RefusedWrites);
    }

    [Fact]
    public async Task In_the_root_reserve_the_system_and_root_keep_writing()
    {
        FullGuard guard = new();
        ServiceFixture fixture = await ServiceFixture.CreateAsync(guard);

        FileSystemMutationResult system = await fixture.WriteFileAsync(fixture.SystemContext);

        Assert.True(system.Succeeded);
        Assert.Equal(1, guard.Refreshes);
    }

    [Fact]
    public async Task Outside_the_reserve_a_users_write_succeeds_and_asks_for_a_new_measure()
    {
        FullGuard guard = new() { IsRootReserveReached = false };
        ServiceFixture fixture = await ServiceFixture.CreateAsync(guard);

        FileSystemMutationResult result = await fixture.WriteFileAsync(fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        Assert.True(result.Succeeded);
        Assert.Equal(1, guard.Refreshes);
    }

    [Fact]
    public async Task Deleting_from_the_trash_asks_the_monitor_to_reclaim_orphan_content()
    {
        Reclaimer reclaimer = new();
        await using Kazo.Os.Platform.Core.Storage.StorageSpaceMonitor monitor = new(new PlentyProbe()) { Reclaimer = reclaimer };
        ServiceFixture fixture = await ServiceFixture.CreateAsync(monitor);
        FileSystemAuthorizationContext user = fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite);
        foreach (string directory in new[] { "/home/user/.local", "/home/user/.local/share", "/home/user/.local/share/Trash", "/home/user/.local/share/Trash/files" })
        {
            await fixture.CreateAsync(directory, FileSystemEntryKind.Directory, user);
        }

        await fixture.CreateAsync("/home/user/.local/share/Trash/files/old.txt", FileSystemEntryKind.File, user);

        // A deletion outside the trash leaves the content to the regular passes.
        Assert.True((await fixture.DeleteAsync("/home/user/file.txt", user)).Succeeded);
        await Task.Delay(Kazo.Os.Platform.Core.Storage.StorageSpaceMonitor.ReclaimRequestDelay * 2);
        Assert.Empty(reclaimer.Calls);

        // Emptying the trash frees content now (ADR 0273 section 4).
        Assert.True((await fixture.DeleteAsync("/home/user/.local/share/Trash/files/old.txt", user)).Succeeded);
        await reclaimer.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([false], reclaimer.Calls);
    }

    private sealed class ServiceFixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;

        private ServiceFixture(IStorageSpaceGuard? spaceGuard)
        {
            Now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            Repository = new InMemoryFileSystemRepository(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                new FixedTimeProvider());
            SystemContext = Context(
                "system",
                AppAuthority.System,
                AppCapabilities.All,
                isSystemOperation: true);
            FileSystemMountRouter router = new(Repository);
            Service = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository()),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                spaceGuard: spaceGuard);
        }

        internal DateTimeOffset Now { get; }
        internal InMemoryFileSystemRepository Repository { get; }
        internal FileSystemService Service { get; }
        internal FileSystemAuthorizationContext SystemContext { get; }

        internal static async Task<ServiceFixture> CreateAsync(IStorageSpaceGuard? spaceGuard = null)
        {
            ServiceFixture fixture = new(spaceGuard);
            FileSystemMutationResult home = await fixture.Repository.CreateAsync(
                CreateDirectory("/home", 1),
                fixture.SystemContext);
            FileSystemMutationResult user = await fixture.Repository.CreateAsync(
                CreateDirectory("/home/user", home.Entry!.Metadata.Revision),
                fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));
            FileSystemMutationResult file = await fixture.Repository.CreateAsync(
                new FileSystemCreateRequest(
                    VirtualPath.Parse("/home/user/file.txt"),
                    FileSystemEntryKind.File,
                    FileSystemPermissions.FromMode(0x01A4),
                    user.Entry!.Metadata.Revision),
                fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));
            await fixture.Repository.WriteAsync(
                new FileSystemWriteRequest(VirtualPath.Parse("/home/user/file.txt"), file.Entry!.Metadata.Revision),
                new BytesSource([7]),
                fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));
            FileSystemEntrySnapshot root = (await fixture.Repository.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse("/")),
                fixture.SystemContext)).Value!;
            await fixture.Repository.CreateAsync(CreateDirectory("/etc", root.Metadata.Revision), fixture.SystemContext);
            return fixture;
        }

        /// <summary>Creates one entry under its existing parent, reading the parent revision first.</summary>
        internal async Task CreateAsync(string path, FileSystemEntryKind kind, FileSystemAuthorizationContext context)
        {
            VirtualPath target = VirtualPath.Parse(path);
            FileSystemEntrySnapshot parent = (await Repository.StatAsync(
                new FileSystemStatRequest(ParentOf(path)),
                SystemContext)).Value!;
            FileSystemMutationResult created = await Repository.CreateAsync(
                new FileSystemCreateRequest(
                    target,
                    kind,
                    FileSystemPermissions.FromMode(kind == FileSystemEntryKind.Directory ? (ushort)0x01FF : (ushort)0x01A4),
                    parent.Metadata.Revision),
                context);
            Assert.True(created.Succeeded);
        }

        private static VirtualPath ParentOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return VirtualPath.Parse(slash == 0 ? "/" : path[..slash]);
        }

        /// <summary>Deletes one file through the service.</summary>
        internal async Task<FileSystemMutationResult> DeleteAsync(string path, FileSystemAuthorizationContext context)
        {
            VirtualPath target = VirtualPath.Parse(path);
            FileSystemEntrySnapshot entry = (await Repository.StatAsync(new FileSystemStatRequest(target), SystemContext)).Value!;
            FileSystemEntrySnapshot parent = (await Repository.StatAsync(
                new FileSystemStatRequest(ParentOf(path)),
                SystemContext)).Value!;
            return await Service.DeleteAsync(
                new FileSystemDeleteRequest(target, entry.Metadata.Revision, parent.Metadata.Revision),
                context);
        }

        /// <summary>Overwrites <c>/home/user/file.txt</c> through the service.</summary>
        internal async Task<FileSystemMutationResult> WriteFileAsync(FileSystemAuthorizationContext context)
        {
            FileSystemEntrySnapshot file = (await Repository.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse("/home/user/file.txt")),
                SystemContext)).Value!;
            return await Service.WriteAsync(
                new FileSystemWriteRequest(VirtualPath.Parse("/home/user/file.txt"), file.Metadata.Revision),
                new BytesSource([1, 2, 3]),
                context);
        }

        internal FileSystemAuthorizationContext UserContext(
            string? capability = null,
            FileSystemSelectedResourceHandle? handle = null) =>
            Context(
                "user",
                AppAuthority.User,
                capability is null ? [] : [capability],
                selectedHandle: handle);

        private FileSystemAuthorizationContext Context(
            string userId,
            AppAuthority authority,
            IEnumerable<string> capabilities,
            bool isSystemOperation = false,
            FileSystemSelectedResourceHandle? selectedHandle = null)
        {
            AppOperationContext operation = new()
            {
                AppId = "org.kazoos.test",
                UserId = userId,
                UserAuthority = authority,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal),
                IsSystemOperation = isSystemOperation
            };
            return new FileSystemAuthorizationContext(operation, [userId == "system" ? "system" : "users"], Now, selectedHandle);
        }

        private static FileSystemCreateRequest CreateDirectory(string path, long parentRevision) =>
            new(
                VirtualPath.Parse(path),
                FileSystemEntryKind.Directory,
                FileSystemPermissions.FromMode(0x01FF),
                parentRevision);
    }

    /// <summary>Records the reclaims the storage monitor runs.</summary>
    private sealed class Reclaimer : IStorageReclaimer
    {
        public List<bool> Calls { get; } = [];

        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<long> ReclaimAsync(bool lowSpace, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add(lowSpace);
            }

            FirstCall.TrySetResult();
            return ValueTask.FromResult(0L);
        }
    }

    /// <summary>A disk with plenty of free space.</summary>
    private sealed class PlentyProbe : IStorageCapacityProbe
    {
        public ValueTask<StorageCapacity?> GetCapacityAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StorageCapacity?>(new StorageCapacity(100L << 20, 1000L << 20));
    }

    /// <summary>Free-space state of the reserve tests; counts the measures the service asked for.</summary>
    private sealed class FullGuard : IStorageSpaceGuard
    {
        public bool IsRootReserveReached { get; init; } = true;

        public int Refreshes { get; private set; }

        public int RefusedWrites { get; private set; }

        public void RequestRefresh() => Refreshes++;

        public void ReportWriteRefused() => RefusedWrites++;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class BytesSource(byte[] bytes) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Binary();
        public long? Length => bytes.LongLength;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}