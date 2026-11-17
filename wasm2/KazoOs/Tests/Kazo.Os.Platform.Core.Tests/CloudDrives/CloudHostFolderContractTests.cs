using System.Text;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.CloudDrives;

/// <summary>
/// A fake cloud service a connector's contract tests run against (ADR 0267, "a contract suite
/// shared by all connectors on a fake HTTP service"). Paths are <c>/a/b</c> from the drive's root.
/// </summary>
public interface ICloudDriveContractService
{
    /// <summary>Gets the account the drive belongs to, in <see cref="CloudHostFolderContractTests.Tokens"/>.</summary>
    string AccountId { get; }

    /// <summary>Gets the size and free space the service reports, or <see langword="null"/> when it has no quota (S3).</summary>
    HostFolderQuota? ExpectedQuota { get; }

    /// <summary>Gets or sets whether the network works (a failure of <c>fetch</c> otherwise).</summary>
    bool Online { get; set; }

    /// <summary>Opens the drive at the root of the account, through the connector's source.</summary>
    ValueTask<IHostFolder> OpenAsync();

    /// <summary>Creates a folder (and its parents) on the service.</summary>
    void SeedFolder(string path);

    /// <summary>Creates or replaces a file (and its parents) on the service, as another device would.</summary>
    void SeedFile(string path, byte[] content);

    /// <summary>Reads a file on the service; <see langword="null"/> when nothing is there.</summary>
    byte[]? ReadRemote(string path);

    /// <summary>Gets whether anything is at <paramref name="path"/> on the service.</summary>
    bool ExistsRemote(string path);

    /// <summary>The service stops accepting the account's current access token (HTTP 401).</summary>
    void RevokeCurrentToken();

    /// <summary>The next <paramref name="count"/> requests answer HTTP <paramref name="status"/> (503, 429…).</summary>
    void FailNext(int count, int status);
}

/// <summary>
/// The contract every cloud connector keeps (ADR 0267 §4, §9 and §10), run against its fake
/// service: listing, reading, creating, writing, conditional writes, deleting, moving, the quota,
/// and how a drive behaves offline, with a refused token, or with an account to reconnect.
/// A connector's test class derives from it and builds its fake in <see cref="CreateService"/>.
/// </summary>
public abstract class CloudHostFolderContractTests
{
    private ICloudDriveContractService? _service;

    /// <summary>Gets the accounts the connector reads its tokens from.</summary>
    protected FakeCloudAccountTokens Tokens { get; } = new();

    /// <summary>Gets the fake service of the test.</summary>
    protected ICloudDriveContractService Service => _service ??= CreateService(Tokens);

    /// <summary>Builds the connector's fake service, with an account added to <paramref name="tokens"/>.</summary>
    protected abstract ICloudDriveContractService CreateService(FakeCloudAccountTokens tokens);

    [Fact]
    public async Task The_root_lists_files_and_folders_with_sizes_and_service_revisions()
    {
        Service.SeedFolder("/Docs");
        Service.SeedFile("/notes.txt", Bytes("hello"));
        IHostFolder folder = await Service.OpenAsync();

        IReadOnlyList<HostFolderEntry> entries = await folder.ListAsync([]);

        Assert.Equal(["Docs", "notes.txt"], entries.Select(static entry => entry.Name).Order(StringComparer.Ordinal));
        HostFolderEntry file = entries.Single(static entry => entry.Name == "notes.txt");
        Assert.Equal(HostFolderEntryKind.File, file.Kind);
        Assert.Equal(5, file.Length);
        Assert.False(string.IsNullOrEmpty(file.Revision));
        Assert.Equal(HostFolderEntryKind.Directory, entries.Single(static entry => entry.Name == "Docs").Kind);
        Assert.Equal(HostFolderEntryKind.Directory, (await folder.StatAsync([])).Kind);
        Assert.Equal(file.Revision, (await folder.StatAsync(["notes.txt"])).Revision);
    }

    [Fact]
    public async Task Missing_entries_and_wrong_kinds_are_reported()
    {
        Service.SeedFolder("/Docs");
        Service.SeedFile("/a.txt", Bytes("a"));
        IHostFolder folder = await Service.OpenAsync();

        Assert.Equal(HostFolderErrorKind.NotFound, (await Fails(() => folder.StatAsync(["nothing"]))).Kind);
        Assert.Equal(HostFolderErrorKind.NotFound, (await Fails(() => folder.ListAsync(["nothing"]))).Kind);
        Assert.Equal(HostFolderErrorKind.TypeMismatch, (await Fails(() => folder.ListAsync(["a.txt"]))).Kind);
        Assert.Equal(HostFolderErrorKind.TypeMismatch, (await Fails(() => folder.ReadAsync(["Docs"]))).Kind);
    }

    [Fact]
    public async Task Files_read_back_whole_including_names_outside_ascii()
    {
        byte[] content = [.. Enumerable.Range(0, 4096).Select(static index => (byte)(index % 251))];
        Service.SeedFile("/Été 2026/Zoë – plan.bin", content);
        IHostFolder folder = await Service.OpenAsync();

        Assert.Equal(content, await folder.ReadAsync(["Été 2026", "Zoë – plan.bin"]));
        Assert.Equal(["Zoë – plan.bin"], (await folder.ListAsync(["Été 2026"])).Select(static entry => entry.Name));
    }

    [Fact]
    public async Task Creating_makes_an_empty_file_or_a_folder_and_refuses_a_taken_name_or_a_missing_parent()
    {
        IHostFolder folder = await Service.OpenAsync();

        await folder.CreateAsync(["Docs"], HostFolderEntryKind.Directory);
        await folder.CreateAsync(["Docs", "new.txt"], HostFolderEntryKind.File);

        Assert.Equal(HostFolderEntryKind.Directory, (await folder.StatAsync(["Docs"])).Kind);
        Assert.Equal(0, (await folder.StatAsync(["Docs", "new.txt"])).Length);
        Assert.Empty(Service.ReadRemote("/Docs/new.txt") ?? [1]);
        Assert.Equal(HostFolderErrorKind.AlreadyExists, (await Fails(() => folder.CreateAsync(["Docs", "new.txt"], HostFolderEntryKind.File))).Kind);
        Assert.Equal(HostFolderErrorKind.AlreadyExists, (await Fails(() => folder.CreateAsync(["Docs"], HostFolderEntryKind.Directory))).Kind);
        Assert.Equal(HostFolderErrorKind.NotFound, (await Fails(() => folder.CreateAsync(["Nope", "x.txt"], HostFolderEntryKind.File))).Kind);
        Assert.False(Service.ExistsRemote("/Nope"));
    }

    [Fact]
    public async Task Writing_replaces_the_content_and_gives_a_new_revision()
    {
        Service.SeedFile("/a.txt", Bytes("old"));
        IHostFolder folder = await Service.OpenAsync();
        string? before = (await folder.StatAsync(["a.txt"])).Revision;

        await folder.WriteAsync(["a.txt"], Bytes("new content"));

        HostFolderEntry after = await folder.StatAsync(["a.txt"]);
        Assert.Equal("new content", Text(Service.ReadRemote("/a.txt")));
        Assert.Equal(11, after.Length);
        Assert.NotEqual(before, after.Revision);
    }

    [Fact]
    public async Task A_conditional_write_succeeds_on_the_current_revision_and_is_refused_after_a_change_elsewhere()
    {
        Service.SeedFile("/a.txt", Bytes("v1"));
        IHostFolder folder = await Service.OpenAsync();
        IConditionalHostFolder conditional = Assert.IsAssignableFrom<IConditionalHostFolder>(folder);
        string revision = (await folder.StatAsync(["a.txt"])).Revision!;

        await conditional.WriteIfMatchAsync(["a.txt"], Bytes("v2"), revision);
        string current = (await folder.StatAsync(["a.txt"])).Revision!;
        Service.SeedFile("/a.txt", Bytes("from another device"));
        HostFolderException refused = await Fails(() => conditional.WriteIfMatchAsync(["a.txt"], Bytes("v3"), current));

        Assert.Equal(HostFolderErrorKind.RevisionConflict, refused.Kind);
        Assert.Equal("from another device", Text(Service.ReadRemote("/a.txt")));
    }

    [Fact]
    public async Task Deleting_refuses_a_non_empty_folder_unless_recursive()
    {
        Service.SeedFile("/Docs/a.txt", Bytes("a"));
        Service.SeedFile("/b.txt", Bytes("b"));
        IHostFolder folder = await Service.OpenAsync();

        await folder.DeleteAsync(["b.txt"], recursive: false);
        HostFolderException refused = await Fails(() => folder.DeleteAsync(["Docs"], recursive: false));
        await folder.DeleteAsync(["Docs"], recursive: true);

        Assert.Equal(HostFolderErrorKind.NotEmpty, refused.Kind);
        Assert.False(Service.ExistsRemote("/b.txt"));
        Assert.False(Service.ExistsRemote("/Docs"));
        Assert.Equal(HostFolderErrorKind.NotFound, (await Fails(() => folder.DeleteAsync(["b.txt"], recursive: false))).Kind);
    }

    [Fact]
    public async Task Moving_renames_into_a_folder_and_refuses_a_taken_name()
    {
        Service.SeedFile("/a.txt", Bytes("a"));
        Service.SeedFile("/taken.txt", Bytes("t"));
        Service.SeedFolder("/Docs");
        IHostFolder folder = await Service.OpenAsync();

        bool moved = await folder.TryMoveAsync(["a.txt"], ["Docs", "renamed.txt"]);
        HostFolderException refused = await Fails(() => folder.TryMoveAsync(["taken.txt"], ["Docs", "renamed.txt"]));

        Assert.True(moved);
        Assert.Equal("a", Text(Service.ReadRemote("/Docs/renamed.txt")));
        Assert.False(Service.ExistsRemote("/a.txt"));
        Assert.Equal(HostFolderErrorKind.AlreadyExists, refused.Kind);
        Assert.Equal("t", Text(Service.ReadRemote("/taken.txt")));
    }

    [Fact]
    public async Task The_quota_is_the_one_the_service_reports()
    {
        IHostFolder folder = await Service.OpenAsync();

        HostFolderQuota? quota = folder is IHostFolderQuota withQuota ? await withQuota.GetQuotaAsync() : null;

        Assert.Equal(Service.ExpectedQuota, quota);
    }

    [Fact]
    public async Task Offline_every_operation_is_unavailable_and_works_again_when_the_network_returns()
    {
        Service.SeedFile("/a.txt", Bytes("a"));
        IHostFolder folder = await Service.OpenAsync();
        Service.Online = false;

        Assert.Equal(HostFolderErrorKind.Unavailable, (await Fails(() => folder.ListAsync([]))).Kind);
        Assert.Equal(HostFolderErrorKind.Unavailable, (await Fails(() => folder.ReadAsync(["a.txt"]))).Kind);
        Assert.Equal(HostFolderErrorKind.Unavailable, (await Fails(() => folder.WriteAsync(["a.txt"], Bytes("b")))).Kind);

        Service.Online = true;
        Assert.Equal("a", Text(await folder.ReadAsync(["a.txt"])));
    }

    [Fact]
    public async Task A_service_down_is_unavailable_and_a_rate_limit_is_a_plain_failure()
    {
        IHostFolder folder = await Service.OpenAsync();

        Service.FailNext(1, 503);
        Assert.Equal(HostFolderErrorKind.Unavailable, (await Fails(() => folder.ListAsync([]))).Kind);
        Service.FailNext(1, 429);
        Assert.Equal(HostFolderErrorKind.Failure, (await Fails(() => folder.ListAsync([]))).Kind);
        Assert.Empty(await folder.ListAsync([]));
    }

    [Fact]
    public async Task A_refused_token_is_renewed_once_and_the_request_goes_through()
    {
        Service.SeedFile("/a.txt", Bytes("a"));
        IHostFolder folder = await Service.OpenAsync();
        Service.RevokeCurrentToken();

        Assert.Equal("a", Text(await folder.ReadAsync(["a.txt"])));
        Assert.Equal(1, Tokens.Renewals);
    }

    [Fact]
    public async Task A_refused_token_that_cannot_be_renewed_offline_makes_the_drive_unavailable()
    {
        IHostFolder folder = await Service.OpenAsync();
        Service.RevokeCurrentToken();
        Tokens.Offline = true;

        Assert.Equal(HostFolderErrorKind.Unavailable, (await Fails(() => folder.ListAsync([]))).Kind);
    }

    [Fact]
    public async Task An_account_to_reconnect_asks_for_the_sign_in_window_and_comes_back_after_it()
    {
        IHostFolder folder = await Service.OpenAsync();
        Assert.Equal(HostFolderPermission.Granted, await folder.QueryPermissionAsync());
        Tokens.RequireReconnect(Service.AccountId);

        Assert.Equal(HostFolderPermission.Prompt, await folder.QueryPermissionAsync());
        Assert.Equal(HostFolderErrorKind.Unavailable, (await Fails(() => folder.ListAsync([]))).Kind);
        Tokens.NextReconnectFails = CloudAccountError.Cancelled;
        Assert.Equal(HostFolderPermission.Prompt, await folder.RequestPermissionAsync());
        Assert.Equal(HostFolderPermission.Granted, await folder.RequestPermissionAsync());

        Assert.Equal(2, Tokens.Reconnections);
        Assert.Empty(await folder.ListAsync([]));
    }

    [Fact]
    public async Task A_blocked_sign_in_window_asks_for_a_user_gesture()
    {
        IHostFolder folder = await Service.OpenAsync();
        Tokens.RequireReconnect(Service.AccountId);
        Tokens.NextReconnectFails = CloudAccountError.WindowBlocked;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await folder.RequestPermissionAsync());
    }

    [Fact]
    public async Task A_removed_account_denies_the_drive()
    {
        IHostFolder folder = await Service.OpenAsync();
        Tokens.Remove(Service.AccountId);

        Assert.Equal(HostFolderPermission.Denied, await folder.QueryPermissionAsync());
        Assert.Equal(HostFolderErrorKind.NotAllowed, (await Fails(() => folder.ListAsync([]))).Kind);
    }

    /// <summary>UTF-8 bytes of <paramref name="text"/>.</summary>
    protected static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Text of UTF-8 bytes, or <see langword="null"/>.</summary>
    protected static string? Text(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);

    /// <summary>Runs an operation that must fail with <see cref="HostFolderException"/>.</summary>
    protected static Task<HostFolderException> Fails<T>(Func<ValueTask<T>> operation) =>
        Assert.ThrowsAsync<HostFolderException>(async () => await operation());

    /// <summary>Runs an operation that must fail with <see cref="HostFolderException"/>.</summary>
    protected static Task<HostFolderException> Fails(Func<ValueTask> operation) =>
        Assert.ThrowsAsync<HostFolderException>(async () => await operation());
}
