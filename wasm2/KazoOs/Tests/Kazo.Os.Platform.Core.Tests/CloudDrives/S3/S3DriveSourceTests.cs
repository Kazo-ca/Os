using System.Net;
using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives.S3;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.CloudAccounts;
using Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives.S3;

/// <summary>
/// The S3 drive source (ADR 0267 §6): a bucket of an S3 compatible service mounts as a network
/// drive, folders are prefixes kept by markers, revisions are ETags with <c>If-Match</c>, the keys
/// live in an <c>s3</c> cloud account sealed on the device, and the drive follows the network.
/// Every request goes to <see cref="FakeS3Server"/>, which checks its SigV4 signature.
/// </summary>
public sealed class S3DriveSourceTests
{
    private const string Read = AppCapabilities.FileSystemSystemRead;
    private const string Write = AppCapabilities.FileSystemSystemWrite;

    [Fact]
    public async Task A_bucket_mounts_under_its_name_with_its_keys_sealed_in_an_s3_account()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "2026/beach.txt", "sand");
        harness.Server.Put("photos", "readme.txt", "hello");

        NetworkDrive? drive = await harness.MountAsync("photos");

        Assert.NotNull(drive);
        Assert.Equal("/mnt/photos", drive.MountPoint.Value);
        Assert.Equal("s3", drive.Type);
        Assert.True(drive.IsConnected);
        Assert.Equal("hello", await harness.Fixture.ReadTextAsync("/mnt/photos/readme.txt", harness.Context));
        Assert.Equal("sand", await harness.Fixture.ReadTextAsync("/mnt/photos/2026/beach.txt", harness.Context));
        Assert.Contains("s3:photos /mnt/photos s3 rw,nosuid,nodev 0 0", await harness.Fixture.ReadTextAsync("/proc/mounts", harness.Context), StringComparison.Ordinal);

        CloudAccount account = Assert.Single(await harness.Accounts.GetAccountsAsync());
        Assert.Equal("s3", account.ProviderId);
        Assert.Equal($"{FakeS3Server.AccessKeyId} — s3.example.test", account.DisplayName);
        Assert.Equal($"{FakeS3Server.AccessKeyId}@s3.example.test", account.ProviderAccountId);

        // Neither list holds the secret; the drive list keeps the account and bucket/prefix only.
        Assert.DoesNotContain(FakeS3Server.SecretAccessKey, (await harness.Settings.ReadAsync(CloudAccountsTable))!, StringComparison.Ordinal);
        Assert.DoesNotContain("s3.example.test/", (await harness.Settings.ReadAsync(CloudAccountsTable))!, StringComparison.Ordinal);
        using JsonDocument table = JsonDocument.Parse(await harness.DriveTableAsync());
        JsonElement line = table.RootElement.GetProperty("drives")[0];
        Assert.Equal(account.Id, line.GetProperty("account").GetString());
        Assert.Equal("photos", line.GetProperty("root").GetString());
        Assert.DoesNotContain(FakeS3Server.SecretAccessKey, table.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeS3Server.AccessKeyId, table.RootElement.GetRawText(), StringComparison.Ordinal);

        // The keyring holds the sealing key of the account, never the S3 secret.
        Assert.Equal(account.Id, Assert.Single(await harness.Settings.Keyring!.ListAsync(harness.Settings.Alice.Id, CloudAccountStore.KeyringOwner)).Id);
        await Assert.ThrowsAsync<CloudAccountException>(() => harness.Accounts.GetAccessTokenAsync(account.Id).AsTask());
        await Assert.ThrowsAsync<CloudAccountException>(() => harness.Accounts.ReconnectAsync(account.Id).AsTask());
    }

    [Fact]
    public async Task Folders_are_prefixes_and_an_emptied_folder_keeps_a_marker()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "2026/a.txt", "a");
        harness.Server.Put("photos", "empty/", string.Empty);
        harness.Server.Put("photos", "top.txt", "t");
        await harness.MountAsync("photos");

        Assert.Equal(["2026", "empty", "top.txt"], await harness.NamesAsync("/mnt/photos"));
        Assert.Empty(await harness.NamesAsync("/mnt/photos/empty"));

        await harness.CreateAsync("/mnt/photos/2026/Trip", FileSystemEntryKind.Directory);
        await harness.CreateAsync("/mnt/photos/2026/Trip/day1.txt", FileSystemEntryKind.File);
        FileSystemMutationResult written = await harness.WriteAsync("/mnt/photos/2026/Trip/day1.txt", "sun");
        Assert.True(written.Succeeded, $"write: {written.Transaction.Error?.Code}");
        Assert.Equal("sun", harness.Server.Text("photos", "2026/Trip/day1.txt"));
        Assert.Contains("2026/Trip/", harness.Server.Keys("photos"));

        FileSystemMutationResult taken = await harness.Fixture.Service.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/mnt/photos/top.txt"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x1A4)), harness.Context);
        Assert.Equal(FileSystemErrorCode.AlreadyExists, taken.Transaction.Error!.Code);

        FileSystemMutationResult notEmpty = await harness.DeleteAsync("/mnt/photos/2026/Trip", recursive: false);
        Assert.Equal(FileSystemErrorCode.DirectoryNotEmpty, notEmpty.Transaction.Error!.Code);

        // The last file of 2026 goes: 2026 stays as a folder thanks to its marker.
        Assert.True((await harness.DeleteAsync("/mnt/photos/2026/Trip", recursive: true)).Succeeded);
        Assert.True((await harness.DeleteAsync("/mnt/photos/2026/a.txt", recursive: false)).Succeeded);
        Assert.Equal(["2026/", "empty/", "top.txt"], harness.Server.Keys("photos"));
        Assert.Empty(await harness.NamesAsync("/mnt/photos/2026"));
    }

    [Fact]
    public async Task A_rename_copies_on_the_service_then_deletes()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "docs/old.txt", "content");
        await harness.MountAsync("photos");
        VirtualPath source = VirtualPath.Parse("/mnt/photos/docs/old.txt");
        VirtualPath parent = VirtualPath.Parse("/mnt/photos/docs");

        long parentRevision = await harness.RevisionAsync(parent);
        FileSystemMutationResult moved = await harness.Fixture.Service.MoveAsync(
            new FileSystemMoveRequest(source, VirtualPath.Parse("/mnt/photos/docs/new.txt"), await harness.RevisionAsync(source), parentRevision, parentRevision),
            harness.Context);

        Assert.True(moved.Succeeded, $"move: {moved.Transaction.Error?.Code}");
        Assert.Equal(["docs/new.txt"], harness.Server.Keys("photos"));
        Assert.Equal("content", harness.Server.Text("photos", "docs/new.txt"));
        Assert.Contains(harness.Server.Received, static request => request.Headers.Contains("x-amz-copy-source"));
    }

    [Fact]
    public async Task The_etag_is_the_revision_and_a_write_sends_if_match()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "v1");
        await harness.MountAsync("photos");
        VirtualPath path = VirtualPath.Parse("/mnt/photos/a.txt");

        long first = await harness.RevisionAsync(path);
        harness.Server.Put("photos", "a.txt", "changed elsewhere");
        Assert.NotEqual(first, await harness.RevisionAsync(path));

        FileSystemMutationResult stale = await harness.WriteAsync(path.Value, "mine", first);
        Assert.Equal(FileSystemErrorCode.RevisionConflict, stale.Transaction.Error!.Code);

        string etag = harness.Server.ETagOf("photos", "a.txt");
        FileSystemMutationResult written = await harness.WriteAsync(path.Value, "mine", await harness.RevisionAsync(path));
        Assert.True(written.Succeeded, $"write: {written.Transaction.Error?.Code}");
        Assert.Equal("mine", harness.Server.Text("photos", "a.txt"));
        HttpRequestMessage put = harness.Server.Received.Last(static request => request.Method == HttpMethod.Put);
        Assert.Equal(etag, Assert.Single(put.Headers.GetValues("If-Match")));
        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);
    }

    [Fact]
    public async Task A_conditional_write_the_service_refuses_is_a_revision_conflict()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "v1");
        IHostFolder folder = await harness.CreateFolderAsync("photos");

        HostFolderException refused = await Assert.ThrowsAsync<HostFolderException>(
            () => ((IConditionalHostFolder)folder).WriteIfMatchAsync(["a.txt"], "x"u8.ToArray(), "\"stale\"").AsTask());

        Assert.Equal(HostFolderErrorKind.RevisionConflict, refused.Kind);
        Assert.Equal("v1", harness.Server.Text("photos", "a.txt"));
    }

    [Fact]
    public async Task A_prefix_drive_sees_only_its_prefix_and_another_bucket_reuses_the_account()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("work", "team/2026/plan.txt", "plan");
        harness.Server.Put("work", "other/secret.txt", "no");
        harness.Server.Put("archive", "old.txt", "old");

        NetworkDrive? team = await harness.MountAsync("work", prefix: "/team/2026/");
        CloudAccount account = Assert.Single(await harness.Accounts.GetAccountsAsync());
        NetworkDrive? archive = await harness.Drives.MountAsync(
            "s3", null, new NetworkDriveRequest(account.Id) { Options = new Dictionary<string, string> { ["bucket"] = "archive" } });

        Assert.Equal("/mnt/work", team!.MountPoint.Value);
        Assert.Equal("work/team/2026", team.Name);
        Assert.Equal(["plan.txt"], await harness.NamesAsync("/mnt/work"));
        Assert.Equal("/mnt/archive", archive!.MountPoint.Value);
        Assert.Equal("old", await harness.Fixture.ReadTextAsync("/mnt/archive/old.txt", harness.Context));
        Assert.Single(await harness.Accounts.GetAccountsAsync());

        // The same keys typed again update the same account rather than adding one.
        await harness.MountAsync("archive");
        Assert.Single(await harness.Accounts.GetAccountsAsync());
    }

    [Fact]
    public async Task Virtual_host_addressing_puts_the_bucket_in_the_host()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "a");

        await harness.MountAsync("photos", addressing: "virtual");
        string text = await harness.Fixture.ReadTextAsync("/mnt/photos/a.txt", harness.Context);

        Assert.Equal("a", text);
        Assert.All(harness.Server.Addresses, static address => Assert.Equal("photos.s3.example.test", address.Host));
    }

    [Theory]
    [InlineData("wrong-secret", "photos", true, "refused the keys")]
    [InlineData(FakeS3Server.SecretAccessKey, "missing", true, "has no bucket 'missing'")]
    [InlineData(FakeS3Server.SecretAccessKey, "photos", false, "CORS rule")]
    public async Task A_refused_mount_says_what_to_fix_and_keeps_no_account(string secret, string bucket, bool online, string reason)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.CreateBucket("photos");
        harness.Server.Online = online;

        NetworkDriveException refused = await Assert.ThrowsAsync<NetworkDriveException>(() => harness.MountAsync(bucket, secret: secret).AsTask());

        Assert.Equal(NetworkDriveError.InvalidRequest, refused.Error);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.Accounts.GetAccountsAsync());
        Assert.Empty(harness.Drives.GetDrives());
    }

    [Fact]
    public async Task Missing_values_and_bad_names_are_refused_before_any_request()
    {
        Harness harness = await Harness.CreateAsync();

        NetworkDriveException noBucket = await Assert.ThrowsAsync<NetworkDriveException>(
            () => harness.Drives.MountAsync("s3", null, new NetworkDriveRequest()).AsTask());
        NetworkDriveException badBucket = await Assert.ThrowsAsync<NetworkDriveException>(() => harness.MountAsync("a").AsTask());
        NetworkDriveException dots = await Assert.ThrowsAsync<NetworkDriveException>(() => harness.MountAsync("photos", prefix: "a/../b").AsTask());

        Assert.Contains("bucket is missing", noBucket.Message, StringComparison.Ordinal);
        Assert.Contains("not a bucket name", badBucket.Message, StringComparison.Ordinal);
        Assert.Contains("not a prefix", dots.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task The_guest_without_a_keyring_cannot_keep_s3_keys()
    {
        Harness harness = await Harness.CreateAsync(signIn: false);
        harness.Server.CreateBucket("photos");

        NetworkDriveException refused = await Assert.ThrowsAsync<NetworkDriveException>(() => harness.MountAsync("photos").AsTask());

        Assert.Equal(NetworkDriveError.InvalidRequest, refused.Error);
        Assert.Contains("keyring", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Losing_the_network_disconnects_the_drive_and_a_retry_reconnects_it()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "a");
        await harness.MountAsync("photos");
        int availability = 0;
        harness.Source.AvailabilityChanged += () => availability++;

        harness.Server.Online = false;
        FileSystemResult<FileSystemContentReadHandle> offline = await harness.Fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/photos/a.txt")), harness.Context);
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, offline.Error!.Code);
        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);

        // Still offline: the retry asks the service, which does not answer, and the drive waits.
        harness.Source.RetryUnreachable();
        Assert.Equal(1, availability);
        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);

        harness.Server.Online = true;
        harness.Source.RetryUnreachable();

        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);
        Assert.Equal("a", await harness.Fixture.ReadTextAsync("/mnt/photos/a.txt", harness.Context));
        availability = 0;
        harness.Source.RetryUnreachable();
        Assert.Equal(0, availability);
    }

    [Fact]
    public async Task The_login_restores_the_drive_without_reaching_the_service()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "a");
        await harness.MountAsync("photos");
        await harness.Drives.UnmountAllAsync();
        harness.Server.Requests.Clear();
        harness.Server.Online = false;

        await harness.Drives.RestoreAsync();

        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);
        Assert.Empty(harness.Server.Requests);
        harness.Server.Online = true;
        Assert.Equal("a", await harness.Fixture.ReadTextAsync("/mnt/photos/a.txt", harness.Context));
    }

    [Fact]
    public async Task Keys_the_service_stops_accepting_disconnect_the_drive_until_they_are_typed_again()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "a");
        await harness.MountAsync("photos");

        harness.Server.ExpectedSecret = "rotated-secret";
        FileSystemResult<FileSystemContentReadHandle> refused = await harness.Fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/photos/a.txt")), harness.Context);
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, refused.Error!.Code);
        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);

        await harness.Drives.ReconnectAvailableAsync();
        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);

        // New keys typed for the same access key and service: the same account, and the drive is back.
        await harness.MountAsync("photos", secret: "rotated-secret");

        Assert.True(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);
        Assert.Single(await harness.Accounts.GetAccountsAsync());
        Assert.Equal("a", await harness.Fixture.ReadTextAsync("/mnt/photos/a.txt", harness.Context));
    }

    [Fact]
    public async Task Keys_that_cannot_be_opened_ask_for_them_again()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "a");
        await harness.MountAsync("photos");
        CloudAccount account = Assert.Single(await harness.Accounts.GetAccountsAsync());
        await harness.Drives.UnmountAllAsync();
        await harness.Settings.Keyring!.DeleteAsync(harness.Settings.Alice.Id, CloudAccountStore.KeyringOwner, account.Id);
        harness.ClearKeys();

        await harness.Drives.RestoreAsync();

        Assert.False(harness.Drives.Find(VirtualPath.Parse("/mnt/photos"))!.IsConnected);
        Assert.Equal(CloudAccountState.NeedsReconnect, Assert.Single(await harness.Accounts.GetAccountsAsync()).State);
    }

    [Fact]
    public async Task Removing_the_account_unmounts_its_drives()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.CreateBucket("photos");
        await harness.MountAsync("photos");
        CloudAccount account = Assert.Single(await harness.Accounts.GetAccountsAsync());

        await harness.Accounts.RemoveAsync(account.Id);

        Assert.Empty(harness.Drives.GetDrives());
        Assert.Empty(await harness.Accounts.GetAccountsAsync());
        Assert.Empty(await harness.Settings.Keyring!.ListAsync(harness.Settings.Alice.Id, CloudAccountStore.KeyringOwner));
    }

    [Fact]
    public async Task A_service_without_conditional_writes_still_writes()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Server.Put("photos", "a.txt", "v1");
        harness.Server.ConditionalWrites = false;
        await harness.MountAsync("photos");
        VirtualPath path = VirtualPath.Parse("/mnt/photos/a.txt");

        FileSystemMutationResult written = await harness.WriteAsync(path.Value, "v2", await harness.RevisionAsync(path));

        Assert.True(written.Succeeded, $"write: {written.Transaction.Error?.Code}");
        Assert.Equal("v2", harness.Server.Text("photos", "a.txt"));
    }

    [Fact]
    public async Task Listings_follow_every_page()
    {
        Harness harness = await Harness.CreateAsync();
        for (int i = 0; i < 7; i++)
        {
            harness.Server.Put("photos", $"f{i}.txt", "x");
        }

        harness.Server.Put("photos", "sub/inner.txt", "x");
        harness.Server.PageSize = 3;
        await harness.MountAsync("photos");

        Assert.Equal(8, (await harness.NamesAsync("/mnt/photos")).Count);
        Assert.True((await harness.DeleteAsync("/mnt/photos/sub", recursive: true)).Succeeded);
    }

    [Fact]
    public async Task An_object_larger_than_the_file_limit_is_listed_but_not_read()
    {
        using HttpClient http = new(new HugeObjectHandler());
        S3Client client = new(http);
        S3Connection connection = S3Connection.Parse("https://s3.example.test", null, null, "key", "secret");

        HostFolderException refused = await Assert.ThrowsAsync<HostFolderException>(
            () => client.GetAsync(connection, "photos", "film.mkv", CancellationToken.None).AsTask());

        Assert.Equal(HostFolderErrorKind.FileTooLarge, refused.Kind);
    }

    private const string CloudAccountsTable = "/home/alice/.local/state/kazoos/cloud-accounts.json";

    /// <summary>An S3 object announced at 200 MiB.</summary>
    private sealed class HugeObjectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream([1, 2, 3])) };
            response.Content.Headers.ContentLength = 200L * 1024 * 1024;
            return Task.FromResult(response);
        }
    }

    private sealed class Harness
    {
        private Harness(VirtualFileSystemFixture fixture, SettingsFilesHarness settings)
        {
            Fixture = fixture;
            Settings = settings;
            FakeOAuthServer oauth = new();
            Accounts = new CloudAccountService(
                settings.FileSystem,
                settings.Clock,
                settings.Keyring!,
                new ManagedKeyringCipher(),
                new EmptyCatalog(),
                oauth,
                new CloudOAuthTokenClient(new HttpClient(oauth)),
                () => settings.Session.CurrentPrincipal,
                () => Drives);
            Source = new S3DriveSource(Accounts, new HttpClient(Server));
            Principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
            Drives = new NetworkDriveService(fixture.Router, [Source], () => Principal);
            Context = fixture.AppContext(null, Read, Write);
        }

        public VirtualFileSystemFixture Fixture { get; }

        public SettingsFilesHarness Settings { get; }

        public FakeS3Server Server { get; } = new();

        public CloudAccountService Accounts { get; }

        public S3DriveSource Source { get; }

        public AuthenticatedPrincipal Principal { get; }

        public NetworkDriveService Drives { get; }

        public FileSystemAuthorizationContext Context { get; }

        public static async Task<Harness> CreateAsync(bool signIn = true)
        {
            SettingsFilesHarness settings = signIn ? new(withKeyring: true) : new(withAccounts: false, withKeyring: true);
            if (signIn)
            {
                await settings.LoginAsync(settings.Alice);
            }
            else
            {
                // The guest has no password, so no open keyring (ADR 0187).
                GuestAccountService guests = new(settings.Users, settings.Groups, settings.Audit, fileSystem: settings.FileSystem, session: settings.Session, iterations: 10);
                LocalUser guest = await guests.CreateAsync("Guest");
                await settings.Session.LoginAsync(guest.LoginName, password: null);
            }

            return new Harness(await CreateSeededAsync(), settings);
        }

        public ValueTask<NetworkDrive?> MountAsync(
            string bucket,
            string? prefix = null,
            string secret = FakeS3Server.SecretAccessKey,
            string? addressing = "path")
        {
            Dictionary<string, string> options = new()
            {
                ["endpoint"] = "https://s3.example.test",
                ["region"] = FakeS3Server.Region,
                ["bucket"] = bucket
            };
            if (prefix is not null)
            {
                options["prefix"] = prefix;
            }

            if (addressing is not null)
            {
                options["addressing"] = addressing;
            }

            return Drives.MountAsync("s3", null, new NetworkDriveRequest
            {
                Options = options,
                Secrets = new Dictionary<string, string>
                {
                    ["access-key-id"] = FakeS3Server.AccessKeyId,
                    ["secret-access-key"] = secret
                }
            });
        }

        public async Task<IHostFolder> CreateFolderAsync(string bucket)
        {
            NetworkDriveCreation? creation = await Source.CreateAsync(new NetworkDriveRequest
            {
                Options = new Dictionary<string, string> { ["endpoint"] = "https://s3.example.test", ["bucket"] = bucket, ["addressing"] = "path" },
                Secrets = new Dictionary<string, string> { ["access-key-id"] = FakeS3Server.AccessKeyId, ["secret-access-key"] = FakeS3Server.SecretAccessKey }
            });
            return creation!.Folder;
        }

        /// <summary>Drops the keys the source holds in memory, as a new session would.</summary>
        public void ClearKeys() => typeof(S3DriveSource)
            .GetMethod("ClearConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(Source, null);

        public Task<string> DriveTableAsync() =>
            Fixture.ReadTextAsync("/home/alice/" + NetworkDriveService.TableRelativePath, Fixture.SystemContext());

        public async Task<List<string>> NamesAsync(string path)
        {
            FileSystemResult<FileSystemDirectorySnapshot> listed = await Fixture.Service.EnumerateAsync(
                new FileSystemEnumerateRequest(VirtualPath.Parse(path)), Context);
            Assert.True(listed.Succeeded, $"list {path}: {listed.Error?.Code}");
            return [.. listed.Value!.Entries.Select(static entry => entry.Name.Value).Order(StringComparer.Ordinal)];
        }

        public async Task<long> RevisionAsync(VirtualPath path)
        {
            FileSystemResult<FileSystemEntrySnapshot> stat = await Fixture.Service.StatAsync(
                new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), Context);
            Assert.True(stat.Succeeded, $"stat {path}: {stat.Error?.Code}");
            return stat.Value!.Metadata.Revision;
        }

        public async Task CreateAsync(string path, FileSystemEntryKind kind)
        {
            FileSystemMutationResult created = await Fixture.Service.CreateAsync(
                new FileSystemCreateRequest(VirtualPath.Parse(path), kind, FileSystemPermissions.FromMode((ushort)(kind == FileSystemEntryKind.Directory ? 0x1ED : 0x1A4))),
                Context);
            Assert.True(created.Succeeded, $"create {path}: {created.Transaction.Error?.Code}");
        }

        public async Task<FileSystemMutationResult> WriteAsync(string path, string text, long? expectedRevision = null)
        {
            VirtualPath target = VirtualPath.Parse(path);
            long revision = expectedRevision ?? await RevisionAsync(target);
            return await Fixture.Service.WriteAsync(
                new FileSystemWriteRequest(target, revision), new BytesSource(Encoding.UTF8.GetBytes(text)), Context);
        }

        public async Task<FileSystemMutationResult> DeleteAsync(string path, bool recursive)
        {
            VirtualPath target = VirtualPath.Parse(path);
            return await Fixture.Service.DeleteAsync(
                new FileSystemDeleteRequest(target, await RevisionAsync(target), await RevisionAsync(VirtualPath.Parse(path[..path.LastIndexOf('/')])), recursive),
                Context);
        }
    }

    private sealed class EmptyCatalog : ICloudProviderCatalog
    {
        public ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CloudOAuthProvider>>([]);
    }
}
