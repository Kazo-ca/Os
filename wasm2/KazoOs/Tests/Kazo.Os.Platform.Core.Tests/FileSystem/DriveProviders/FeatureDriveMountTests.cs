using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.CloudDrives;
using Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.DriveProviders;

/// <summary>
/// The network drive service with the types features provide (ADR 0282 §3): a type installed while
/// the page runs is offered at once, its drives mount like any other, and a login never waits for
/// a slow provider: the drive is mounted disconnected and reconnects when the provider answers.
/// </summary>
public sealed class FeatureDriveMountTests : IDisposable
{
    private const string AccountId = "mem-alice";
    private readonly FakeCloudAccountTokens _tokens = new();
    private readonly InMemoryDriveService _service = new();

    public FeatureDriveMountTests() => _tokens.Add(AccountId, DriveProviderFixtures.Type, displayName: "alice@example.test");

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task A_type_installed_while_the_page_runs_is_offered_at_once_and_its_drive_mounts()
    {
        _service.SeedFile("/Docs/a.txt", Encoding.UTF8.GetBytes("from memory"));
        Harness harness = await Harness.CreateAsync(_tokens, _service);
        Assert.DoesNotContain(DriveProviderFixtures.Type, harness.Drives.SourceTypes);

        harness.Page.Install(DriveProviderFixtures.Feature());
        NetworkDrive? drive = await harness.Drives.MountAsync(DriveProviderFixtures.Type, null, new NetworkDriveRequest(AccountId));
        await harness.ConnectedAsync(drive!.MountPoint);

        Assert.Contains(DriveProviderFixtures.Type, harness.Drives.SourceTypes);
        Assert.Equal("/mnt/Memory", drive.MountPoint.Value);
        Assert.Equal((DriveProviderFixtures.Type, AccountId), (drive.Type, drive.Account));
        Assert.Equal("from memory", await harness.Fixture.ReadTextAsync("/mnt/Memory/Docs/a.txt", harness.Context));
    }

    [Fact]
    public async Task A_login_never_waits_for_a_slow_provider_and_its_drive_reconnects_when_it_answers()
    {
        _service.SeedFile("/a.txt", Encoding.UTF8.GetBytes("later"));
        Harness harness = await Harness.CreateAsync(_tokens, _service, DriveProviderFixtures.Feature());
        // A drive saved in the user's list, then a logout.
        NetworkDrive? saved = await harness.Drives.MountAsync(DriveProviderFixtures.Type, null, new NetworkDriveRequest(AccountId));
        await harness.ConnectedAsync(saved!.MountPoint);
        await harness.Drives.UnmountAllAsync();
        _service.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // What the session's activation runs at login.
        Stopwatch clock = Stopwatch.StartNew();
        await harness.Drives.RestoreAsync();
        clock.Stop();

        NetworkDrive restored = Assert.Single(harness.Drives.GetDrives());
        Assert.False(restored.IsConnected);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the login waited {clock.Elapsed}");

        _service.OpenGate.SetResult();
        await harness.ConnectedAsync(restored.MountPoint);
        Assert.Equal("later", await harness.Fixture.ReadTextAsync("/mnt/Memory/a.txt", harness.Context));
    }

    [Fact]
    public async Task A_drive_whose_feature_is_disabled_or_removed_is_kept_unmounted_and_mounts_again_when_it_returns()
    {
        _service.SeedFile("/a.txt", Encoding.UTF8.GetBytes("kept"));
        Harness harness = await Harness.CreateAsync(_tokens, _service, DriveProviderFixtures.Feature());
        NetworkDrive? drive = await harness.Drives.MountAsync(DriveProviderFixtures.Type, null, new NetworkDriveRequest(AccountId));
        await harness.ConnectedAsync(drive!.MountPoint);
        Assert.Equal(DriveProviderFixtures.FeatureId, drive.ProviderFeatureId);
        Assert.Equal(DriveProviderFixtures.FeatureId, ProviderOf(await harness.TableAsync()));

        harness.Page.Enablement.MarkDisabled([DriveProviderFixtures.FeatureId]);
        await harness.Drives.RefreshProvidersAsync();

        Assert.Empty(harness.Drives.GetDrives());
        NetworkDrive missing = Assert.Single(harness.Drives.GetProviderMissingDrives());
        Assert.Equal(("/mnt/Memory", true, false), (missing.MountPoint.Value, missing.IsProviderMissing, missing.IsConnected));
        Assert.Equal((DriveProviderFixtures.Type, DriveProviderFixtures.FeatureId), (missing.Type, missing.ProviderFeatureId));
        Assert.Equal(DriveProviderFixtures.FeatureId, ProviderOf(await harness.TableAsync()));

        harness.Page.Enablement.MarkEnabled(DriveProviderFixtures.FeatureId);
        await harness.Drives.RefreshProvidersAsync();
        await harness.ConnectedAsync(drive.MountPoint);
        Assert.Empty(harness.Drives.GetProviderMissingDrives());

        harness.Page.Install();
        await harness.Drives.RefreshProvidersAsync();
        Assert.Empty(harness.Drives.GetDrives());
        Assert.Single(harness.Drives.GetProviderMissingDrives());

        harness.Page.Install(DriveProviderFixtures.Feature(version: "1.1.0"));
        await harness.Drives.RefreshProvidersAsync();
        await harness.ConnectedAsync(drive.MountPoint);
        Assert.Equal("kept", await harness.Fixture.ReadTextAsync("/mnt/Memory/a.txt", harness.Context));
    }

    [Fact]
    public async Task Another_feature_that_provides_the_same_type_does_not_take_the_drive_over()
    {
        Harness harness = await Harness.CreateAsync(_tokens, _service, DriveProviderFixtures.Feature());
        NetworkDrive? drive = await harness.Drives.MountAsync(DriveProviderFixtures.Type, null, new NetworkDriveRequest(AccountId));
        await harness.ConnectedAsync(drive!.MountPoint);

        harness.Page.Install(DriveProviderFixtures.Feature(id: "org.test.other"));
        await harness.Drives.RefreshProvidersAsync();
        Assert.Empty(harness.Drives.GetDrives());

        // Not at the next login either.
        await harness.Drives.UnmountAllAsync();
        await harness.Drives.RestoreAsync();

        Assert.Empty(harness.Drives.GetDrives());
        NetworkDrive missing = Assert.Single(harness.Drives.GetProviderMissingDrives());
        Assert.Equal(DriveProviderFixtures.FeatureId, missing.ProviderFeatureId);
        Assert.Equal(DriveProviderFixtures.FeatureId, ProviderOf(await harness.TableAsync()));
    }

    [Fact]
    public async Task At_login_a_line_noted_with_its_feature_is_kept_and_a_line_of_a_type_nothing_provided_is_dropped()
    {
        Harness harness = await Harness.CreateAsync(_tokens, _service, DriveProviderFixtures.Feature());
        // A first drive writes the list (and its folder); then the feature is removed and a logout.
        NetworkDrive? first = await harness.Drives.MountAsync(DriveProviderFixtures.Type, null, new NetworkDriveRequest(AccountId));
        await harness.ConnectedAsync(first!.MountPoint);
        await harness.Drives.UnmountAllAsync();
        harness.Page.Install();
        await harness.WriteTableAsync($$"""
            {
              "schemaVersion": 1,
              "drives": [
                { "mountPoint": "/mnt/Kept", "name": "Kept", "createdMountPoint": true, "type": "{{DriveProviderFixtures.Type}}", "account": "{{AccountId}}", "provider": "{{DriveProviderFixtures.FeatureId}}" },
                { "mountPoint": "/mnt/Gone", "name": "Gone", "createdMountPoint": true, "type": "{{DriveProviderFixtures.Type}}", "account": "{{AccountId}}" }
              ]
            }
            """);

        await harness.Drives.RestoreAsync();

        Assert.Empty(harness.Drives.GetDrives());
        NetworkDrive kept = Assert.Single(harness.Drives.GetProviderMissingDrives());
        Assert.Equal(("/mnt/Kept", DriveProviderFixtures.FeatureId), (kept.MountPoint.Value, kept.ProviderFeatureId));
        string table = await harness.TableAsync();
        Assert.Contains("/mnt/Kept", table, StringComparison.Ordinal);
        Assert.DoesNotContain("/mnt/Gone", table, StringComparison.Ordinal);

        harness.Page.Install(DriveProviderFixtures.Feature());
        await harness.Drives.RefreshProvidersAsync();
        await harness.ConnectedAsync(kept.MountPoint);
    }

    /// <summary>Gets the feature noted in the first line of the drive list.</summary>
    private static string? ProviderOf(string table)
    {
        using JsonDocument document = JsonDocument.Parse(table);
        return document.RootElement.GetProperty("drives")[0].TryGetProperty("provider", out JsonElement provider) ? provider.GetString() : null;
    }

    private sealed class Harness
    {
        private Harness(VirtualFileSystemFixture fixture, FakeCloudAccountTokens tokens, InMemoryDriveService service, AppManifest[] manifests)
        {
            Fixture = fixture;
            Principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
            Page = new DriveProviderFixtures.Page(tokens, _ => service, manifests);
            Drives = new NetworkDriveService(fixture.Router, [], Page.Sources, () => Principal);
            Context = fixture.AppContext(null, AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite);
        }

        public VirtualFileSystemFixture Fixture { get; }

        public DriveProviderFixtures.Page Page { get; }

        public AuthenticatedPrincipal Principal { get; }

        public NetworkDriveService Drives { get; }

        public FileSystemAuthorizationContext Context { get; }

        private static string TablePath => "/home/alice/" + NetworkDriveService.TableRelativePath;

        public Task<string> TableAsync() => Fixture.ReadTextAsync(TablePath, Fixture.SystemContext());

        public async Task WriteTableAsync(string json)
        {
            FileSystemMutationResult written = await Fixture.Service.WriteAsync(
                new FileSystemWriteRequest(VirtualPath.Parse(TablePath)), new BytesSource(Encoding.UTF8.GetBytes(json)), Fixture.SystemContext());
            Assert.True(written.Succeeded, $"table: {written.Transaction.Error?.Code}");
        }

        public static async Task<Harness> CreateAsync(FakeCloudAccountTokens tokens, InMemoryDriveService service, params AppManifest[] manifests) =>
            new(await CreateSeededAsync(), tokens, service, manifests);

        /// <summary>Waits (bounded) until the drive at <paramref name="mountPoint"/> is connected.</summary>
        public async Task ConnectedAsync(VirtualPath mountPoint)
        {
            TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check()
            {
                if (Drives.Find(mountPoint) is { IsConnected: true })
                {
                    connected.TrySetResult();
                }
            }

            Drives.Changed += Check;
            try
            {
                Check();
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Drives.Changed -= Check;
            }
        }
    }
}
