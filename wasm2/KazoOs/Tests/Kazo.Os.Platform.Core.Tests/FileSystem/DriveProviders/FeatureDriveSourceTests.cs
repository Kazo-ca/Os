using System.Diagnostics;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.Platform.Core.FileSystem.DriveProviders;
using Kazo.Os.Platform.Core.Tests.CloudDrives;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.DriveProviders;

/// <summary>
/// The adapter of a provided drive type (ADR 0282 §3): loaded at the first drive, once per page,
/// in the feature's context; a provider that fails, throws or is slow leaves the drive disconnected
/// with a line in the feature's log, and a login never waits for it.
/// </summary>
public sealed class FeatureDriveSourceTests : IDisposable
{
    private const string AccountId = "mem-bob";
    private readonly FakeCloudAccountTokens _tokens = new();
    private readonly InMemoryDriveService _service = new();

    public FeatureDriveSourceTests() => _tokens.Add(AccountId, DriveProviderFixtures.Type, displayName: "bob@example.test");

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task Opening_a_drive_never_waits_for_a_slow_provider_and_the_drive_reconnects_when_it_answers()
    {
        _service.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.SeedFile("/a.txt", "a"u8.ToArray());
        using DriveProviderFixtures.Page page = Page();
        FeatureDriveSource source = page.Source;
        TaskCompletionSource available = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AvailabilityChanged += () => available.TrySetResult();

        // What a login does for a saved drive: open it and ask whether it is reachable.
        Stopwatch clock = Stopwatch.StartNew();
        IHostFolder folder = (await source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;
        HostFolderPermission atLogin = await folder.QueryPermissionAsync();
        clock.Stop();

        Assert.Equal(HostFolderPermission.Unavailable, atLogin);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the login waited {clock.Elapsed}");
        Assert.False(available.Task.IsCompleted);

        _service.OpenGate.SetResult();
        await available.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HostFolderPermission.Granted, await folder.QueryPermissionAsync());
        Assert.Equal("a"u8.ToArray(), await folder.ReadAsync(["a.txt"]));
    }

    [Fact]
    public async Task A_provider_that_does_not_open_in_time_leaves_the_drive_disconnected_and_writes_the_feature_log()
    {
        _service.OpenGate = new TaskCompletionSource();
        using DriveProviderFixtures.Page page = Page(TimeSpan.FromMilliseconds(200));
        IHostFolder folder = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;

        HostFolderException refused = await Assert.ThrowsAsync<HostFolderException>(async () => await folder.ListAsync([]));

        Assert.Equal(HostFolderErrorKind.Unavailable, refused.Kind);
        Assert.Contains(page.FeatureLog, line => line.Feature == DriveProviderFixtures.FeatureId && line.Message.Contains("did not open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_whose_constructor_throws_is_a_disconnected_drive_and_is_tried_again_at_the_next_drive()
    {
        using DriveProviderFixtures.Page page = Page(manifests: [DriveProviderFixtures.Feature(provider: typeof(ThrowingDriveProvider))]);
        IHostFolder first = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;

        HostFolderException refused = await Assert.ThrowsAsync<HostFolderException>(async () => await first.ListAsync([]));
        IHostFolder second = (await page.Source.OpenAsync(new NetworkDriveReference("Memory 2", Account: AccountId)))!;
        await Assert.ThrowsAsync<HostFolderException>(async () => await second.ListAsync([]));

        Assert.Equal(HostFolderErrorKind.Unavailable, refused.Kind);
        Assert.Contains(page.FeatureLog, line => line.Message.Contains("InvalidOperationException: the provider is broken", StringComparison.Ordinal));
        // A failure is never cached: the second drive loaded the provider again.
        Assert.Equal(2, page.Started.Count);
    }

    [Fact]
    public async Task A_missing_assembly_or_a_process_that_does_not_start_is_written_to_the_feature_log()
    {
        using DriveProviderFixtures.Page page = Page();
        page.AssemblyFails = true;
        IHostFolder folder = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;
        await Assert.ThrowsAsync<HostFolderException>(async () => await folder.ListAsync([]));
        page.AssemblyFails = false;
        page.ProcessFails = true;
        IHostFolder again = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;
        await Assert.ThrowsAsync<HostFolderException>(async () => await again.ListAsync([]));

        Assert.Contains(page.FeatureLog, line => line.Message.Contains("could not be loaded", StringComparison.Ordinal));
        Assert.Contains(page.FeatureLog, line => line.Message.Contains("service process did not start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_provider_is_created_once_per_page_in_the_process_and_context_of_its_feature()
    {
        using DriveProviderFixtures.Page page = Page(manifests: [DriveProviderFixtures.Feature(provider: typeof(ContextDriveProvider))]);
        IHostFolder first = (await page.Source.OpenAsync(new NetworkDriveReference("One", Account: AccountId)))!;
        IHostFolder second = (await page.Source.OpenAsync(new NetworkDriveReference("Two", Account: AccountId)))!;

        await first.ListAsync([]);
        await second.ListAsync([]);

        (string appId, Type implementation) = Assert.Single(page.Started);
        Assert.Equal(DriveProviderFixtures.FeatureId, appId);
        Assert.Equal(typeof(ContextDriveProvider), implementation);
        Assert.Equal(DriveProviderFixtures.FeatureId, ContextDriveProvider.LastContext?.Feature.Manifest.Id);
        Assert.Equal(2, _service.Opened);
    }

    [Fact]
    public async Task A_drive_needs_an_account_of_its_own_type_and_another_type_is_denied()
    {
        _tokens.Add("dbx-carol", "dropbox");
        using DriveProviderFixtures.Page page = Page();

        await Assert.ThrowsAsync<ArgumentException>(async () => await page.Source.CreateAsync(new NetworkDriveRequest("dbx-carol")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await page.Source.CreateAsync(new NetworkDriveRequest()));
        IHostFolder borrowed = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: "dbx-carol")))!;
        Assert.Null(await page.Source.OpenAsync(new NetworkDriveReference("Memory")));

        Assert.Equal(HostFolderPermission.Denied, await borrowed.QueryPermissionAsync());
        Assert.Equal(0, _service.Opened);
    }

    /// <summary>
    /// A drive the user connects now is opened before it is mounted, so it mounts connected and the
    /// File Explorer lists it at once (found by the trimmed OS journey: the explorer showed
    /// ProviderUnavailable because the new drive was mounted disconnected). A login still never waits.
    /// </summary>
    [Fact]
    public async Task Creating_a_drive_names_it_after_the_account_and_opens_the_provider_before_the_mount()
    {
        _service.SeedFile("/Docs/a.txt", "a"u8.ToArray());
        using DriveProviderFixtures.Page page = Page();

        NetworkDriveCreation creation = (await page.Source.CreateAsync(new NetworkDriveRequest(AccountId, Root: "/Docs")))!;

        Assert.Equal(new NetworkDriveReference("bob@example.test", Key: null, Account: AccountId, Root: "/Docs"), creation.Reference);
        Assert.Equal("Memory", creation.DefaultMountName);
        Assert.Single(page.Started);
        Assert.Equal(HostFolderPermission.Granted, await creation.Folder.QueryPermissionAsync());
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_refused_before_it_reaches_the_provider()
    {
        using DriveProviderFixtures.Page page = Page();
        IHostFolder folder = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;
        await folder.ListAsync([]);

        HostFolderException tooLarge = await Assert.ThrowsAsync<HostFolderException>(
            async () => await folder.WriteAsync(["big.bin"], new byte[KazoDriveLimits.MaxFileBytes + 1]));

        Assert.Equal(HostFolderErrorKind.FileTooLarge, tooLarge.Kind);
        Assert.False(_service.ExistsRemote("/big.bin"));
    }

    [Fact]
    public async Task The_types_follow_the_catalog_at_once_install_disable_and_update()
    {
        using DriveProviderFixtures.Page page = Page(manifests: []);
        int changes = 0;
        page.Sources.Changed += () => changes++;
        Assert.Empty(page.Sources.GetSources());

        page.Install(DriveProviderFixtures.Feature());
        FeatureDriveSource installed = page.Source;
        Assert.Same(installed, page.Source);
        Assert.Equal(DriveProviderFixtures.FeatureId, page.Sources.FindFeature(DriveProviderFixtures.Type));

        page.Enablement.MarkDisabled([DriveProviderFixtures.FeatureId]);
        Assert.Empty(page.Sources.GetSources());
        Assert.Equal(DriveProviderFixtures.FeatureId, page.Sources.FindFeature(DriveProviderFixtures.Type));

        page.Enablement.MarkEnabled(DriveProviderFixtures.FeatureId);
        page.Install(DriveProviderFixtures.Feature(version: "1.1.0"));
        FeatureDriveSource updated = page.Source;
        Assert.NotSame(installed, updated);
        Assert.Equal("1.1.0", updated.Registration.Feature.Version);

        page.Install();
        Assert.Empty(page.Sources.GetSources());
        Assert.Null(page.Sources.FindFeature(DriveProviderFixtures.Type));
        Assert.True(changes >= 4);
    }

    [Fact]
    public async Task An_open_drive_allows_the_typed_server_to_its_feature_until_it_closes()
    {
        using DriveProviderFixtures.Page page = Page(manifests: [DriveProviderFixtures.Feature(serverAddress: true)]);
        page.ExtraOptions[DriveProviderFixtures.ServerOption] = "https://dav.example.org/remote.php/dav/files/bob/";
        IHostFolder first = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;
        IHostFolder second = (await page.Source.OpenAsync(new NetworkDriveReference("Memory 2", Account: AccountId)))!;
        await first.ListAsync([]);
        await second.ListAsync([]);

        bool whileOpen = page.ServerHosts.IsAllowed(DriveProviderFixtures.FeatureId, "dav.example.org");
        bool toAnother = page.ServerHosts.IsAllowed("org.test.other", "dav.example.org");
        await first.DisposeAsync();
        bool whileOneIsOpen = page.ServerHosts.IsAllowed(DriveProviderFixtures.FeatureId, "dav.example.org");
        await second.DisposeAsync();

        Assert.True(whileOpen);
        Assert.False(toAnother);
        Assert.True(whileOneIsOpen);
        Assert.False(page.ServerHosts.IsAllowed(DriveProviderFixtures.FeatureId, "dav.example.org"));
    }

    [Theory]
    [InlineData("http://dav.example.org/")]
    [InlineData("https://192.168.1.20/")]
    [InlineData("https://localhost/")]
    public async Task A_server_the_os_may_not_allow_opens_the_drive_but_allows_nothing(string address)
    {
        using DriveProviderFixtures.Page page = Page(manifests: [DriveProviderFixtures.Feature(serverAddress: true)]);
        page.ExtraOptions[DriveProviderFixtures.ServerOption] = address;
        IHostFolder folder = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;

        await folder.ListAsync([]);

        Assert.False(page.ServerHosts.IsAllowed(DriveProviderFixtures.FeatureId, new Uri(address).Host));
    }

    [Fact]
    public async Task Without_a_server_address_field_a_typed_value_allows_nothing()
    {
        using DriveProviderFixtures.Page page = Page();
        page.ExtraOptions[DriveProviderFixtures.ServerOption] = "https://dav.example.org/";
        IHostFolder folder = (await page.Source.OpenAsync(new NetworkDriveReference("Memory", Account: AccountId)))!;

        await folder.ListAsync([]);

        Assert.False(page.ServerHosts.IsAllowed(DriveProviderFixtures.FeatureId, "dav.example.org"));
    }

    private DriveProviderFixtures.Page Page(TimeSpan? openTimeout = null, Kazo.Os.App.Abstractions.AppManifest[]? manifests = null) =>
        new(_tokens, _ => _service, openTimeout ?? FeatureDriveSource.DefaultOpenTimeout, manifests ?? [DriveProviderFixtures.Feature()]);
}
