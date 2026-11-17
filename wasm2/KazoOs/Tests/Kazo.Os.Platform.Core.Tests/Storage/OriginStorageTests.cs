using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Storage;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Storage;
using Kazo.Os.Platform.Core.Tests.Features;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Storage;

/// <summary>
/// ADR 0250: <c>persist()</c> once for the origin, the boot marker that finds an eviction of the OS's
/// data, and the marker of a feature whose Origin Private File System the browser erased. The feature
/// of fixture keeps a disk in the Origin Private File System, like a virtual machine's.
/// </summary>
public sealed class OriginStorageTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset LastBoot = new(2026, 11, 10, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_marker_without_the_database_is_an_eviction_with_the_date_of_the_last_boot()
    {
        FakeOriginStorage storage = new() { Marker = StorageEvictionDetector.Format(LastBoot, persisted: false) };
        StorageEvictionDetector detector = new(storage);

        StorageEviction? eviction = await detector.DetectAsync();

        Assert.NotNull(eviction);
        Assert.Equal(LastBoot, eviction.LastBootUtc);
        Assert.Same(eviction, detector.Eviction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, null)]
    public async Task No_eviction_without_a_marker_or_with_the_database_or_when_the_browser_cannot_tell(bool hasMarker, bool? databaseExists)
    {
        FakeOriginStorage storage = new()
        {
            Marker = hasMarker ? StorageEvictionDetector.Format(LastBoot, persisted: false) : null,
            DatabaseExists = databaseExists,
        };
        StorageEvictionDetector detector = new(storage);

        Assert.Null(await detector.DetectAsync());
        Assert.Null(detector.Eviction);
    }

    [Fact]
    public async Task A_successful_boot_writes_the_marker()
    {
        FakeOriginStorage storage = new() { DatabaseExists = false };
        StorageEvictionDetector detector = new(storage);

        await detector.RecordBootAsync();

        (DateTimeOffset? lastBoot, bool persisted) = StorageEvictionDetector.Read(storage.Marker);
        Assert.NotNull(lastBoot);
        Assert.False(persisted);
    }

    [Fact]
    public async Task Persistence_withdrawn_by_the_browser_is_noticed_at_the_next_boot()
    {
        FakeOriginStorage storage = new() { Marker = StorageEvictionDetector.Format(LastBoot, persisted: true) };
        StorageEvictionDetector detector = new(storage);

        await detector.RecordBootAsync();

        Assert.True(detector.PersistenceLost);
        Assert.False(StorageEvictionDetector.Read(storage.Marker).Persisted);
    }

    [Fact]
    public async Task RequestPersistenceAsync_calls_persist_once_for_the_origin()
    {
        Fixture fixture = await Fixture.CreateAsync();

        bool first = await fixture.Service.RequestPersistenceAsync();
        bool second = await fixture.Service.RequestPersistenceAsync();

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, fixture.Storage.PersistCalls);
    }

    [Fact]
    public async Task An_origin_already_persisted_is_not_asked_again()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Storage.Persisted = true;

        Assert.True(await fixture.Service.RequestPersistenceAsync());
        Assert.Equal(0, fixture.Storage.PersistCalls);
    }

    [Fact]
    public async Task An_ordinary_app_may_not_ask_for_persistence_nor_register_a_marker()
    {
        Fixture fixture = await Fixture.CreateAsync();
        AppStorageGateway gateway = fixture.GatewayFor(FeaturePermissionFixtures.Client());

        KazoUnauthorizedAccessException refused = await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
            () => gateway.RequestPersistenceAsync().AsTask());
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => gateway.RegisterMarkerAsync("x/marker").AsTask());
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => gateway.ReportUsageAsync(1).AsTask());

        Assert.Contains(AppStorageGateway.FeatureOnlyCode, refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Storage.PersistCalls);
    }

    [Fact]
    public async Task A_system_app_may_ask_for_persistence()
    {
        Fixture fixture = await Fixture.CreateAsync();
        AppStorageGateway settings = fixture.GatewayFor(FeaturePermissionFixtures.Client(), isSystemApp: true);

        Assert.True(await settings.RequestPersistenceAsync());
        Assert.Equal(1, fixture.Storage.PersistCalls);
    }

    [Fact]
    public async Task A_feature_whose_disk_the_browser_erased_is_told_at_its_next_launch_and_the_user_is_notified()
    {
        Fixture fixture = await Fixture.CreateAsync();
        DiskFeature feature = new(fixture);
        await feature.CreateDiskAsync(3 * GiB);

        // The browser erases the Origin Private File System between two boots.
        fixture.Storage.Files.Clear();
        IReadOnlyList<string> evicted = await fixture.Service.CheckMarkersAsync();

        Assert.Equal([FeaturePermissionFixtures.FeatureId], evicted);
        Notification notification = Assert.Single(fixture.Queue.GetActive(fixture.Alice.UserId, DateTimeOffset.UtcNow));
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Contains("Machines", notification.Message, StringComparison.Ordinal);

        // Its next launch: the change waits for its handler, and is delivered once.
        AppStorageGateway relaunched = fixture.GatewayFor(FeaturePermissionFixtures.Feature());
        List<KazoStorageChangedEventArgs> changes = [];
        relaunched.Changed += (_, change) => changes.Add(change);
        relaunched.Changed += (_, change) => changes.Add(change);

        KazoStorageChangedEventArgs received = Assert.Single(changes);
        Assert.Equal(KazoStorageChangeKind.Evicted, received.Kind);
        Assert.Equal(FeatureDisk.MarkerPath, received.MarkerPath);
        // The marker is forgotten: the next boot does not tell it again.
        Assert.Empty(await fixture.Service.CheckMarkersAsync());
    }

    [Fact]
    public async Task A_disk_still_there_is_not_an_eviction()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await new DiskFeature(fixture).CreateDiskAsync(GiB);

        Assert.Empty(await fixture.Service.CheckMarkersAsync());
        Assert.Empty(fixture.Queue.GetActive(fixture.Alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_space_a_feature_declares_is_in_its_estimate_and_in_the_list_of_features()
    {
        Fixture fixture = await Fixture.CreateAsync();
        DiskFeature feature = new(fixture);
        await feature.CreateDiskAsync(3 * GiB);

        KazoStorageEstimate estimate = await feature.Gateway.GetEstimateAsync();
        IReadOnlyList<KazoFeatureStorageUsage> features = await fixture.GatewayFor(FeaturePermissionFixtures.Client()).GetFeatureUsageAsync();

        Assert.Equal(3 * GiB, estimate.DeclaredUsageBytes);
        Assert.Equal(12 * GiB, estimate.QuotaBytes);
        Assert.Equal(4 * GiB, estimate.FileSystemBytes);
        KazoFeatureStorageUsage usage = Assert.Single(features);
        Assert.Equal((FeaturePermissionFixtures.FeatureId, "Machines", (long?)(3 * GiB)), (usage.FeatureId, usage.Name, usage.UsageBytes));
        // An app reads the estimate with no declared part of its own.
        Assert.Null((await fixture.GatewayFor(FeaturePermissionFixtures.Client()).GetEstimateAsync()).DeclaredUsageBytes);
    }

    [Theory]
    [InlineData("/machines/marker")]
    [InlineData("machines/../marker")]
    [InlineData("machines//marker")]
    public void A_marker_path_must_stay_inside_the_origin_private_file_system(string path) =>
        Assert.Throws<ArgumentException>(() => OriginStorageService.NormalizeMarkerPath(path));

    [Fact]
    public void The_state_file_reads_back_what_it_wrote_and_skips_broken_entries()
    {
        DateTimeOffset reportedAt = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        FeatureStorageRecord record = new("org.test.machines", "Machines", "machines/.kazoos-marker", 5 * GiB, reportedAt);

        IReadOnlyList<FeatureStorageRecord> read = OriginStorageService.Parse(OriginStorageService.Format([record]));

        Assert.Equal([record], read);
        Assert.Empty(OriginStorageService.Parse("{not json"));
        Assert.Empty(OriginStorageService.Parse("{\"features\":{\"x\":3}}"));
    }

    /// <summary>
    /// The feature of fixture: after the user's first « create », it writes its disk and a marker in the
    /// Origin Private File System, asks for persistence, registers the marker and declares its usage.
    /// </summary>
    private sealed class DiskFeature(Fixture fixture)
    {
        public AppStorageGateway Gateway { get; } = fixture.GatewayFor(FeaturePermissionFixtures.Feature());

        public async Task CreateDiskAsync(long bytes)
        {
            fixture.Storage.Files.Add(FeatureDisk.DiskPath);
            fixture.Storage.Files.Add(FeatureDisk.MarkerPath);
            await Gateway.RequestPersistenceAsync();
            await Gateway.RegisterMarkerAsync(FeatureDisk.MarkerPath);
            await Gateway.ReportUsageAsync(bytes);
        }
    }

    private static class FeatureDisk
    {
        public const string DiskPath = "machines/vm1/disk.img";
        public const string MarkerPath = "machines/.kazoos-marker";
    }

    private sealed class Fixture
    {
        private Fixture(SettingsFilesHarness settings, AuthenticatedPrincipal alice)
        {
            Alice = alice;
            Service = new OriginStorageService(Storage, settings.FileSystem, settings.Clock, notifications: Queue, currentUser: () => Alice);
        }

        public AuthenticatedPrincipal Alice { get; }

        public FakeOriginStorage Storage { get; } = new()
        {
            Status = new OriginStorageStatus(5 * GiB, 12 * GiB, false) { IndexedDbBytes = GiB / 2, CacheBytes = GiB / 2, FileSystemBytes = 4 * GiB },
        };

        public InMemoryNotificationQueue Queue { get; } = new(maxEntriesPerUser: 10);

        public OriginStorageService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            SettingsFilesHarness settings = new();
            await settings.BootAsync();
            AuthenticatedPrincipal alice = await settings.LoginAsync(settings.Alice);
            return new Fixture(settings, alice);
        }

        public AppStorageGateway GatewayFor(AppManifest manifest, bool isSystemApp = false) => new(() => Service, manifest, isSystemApp);
    }

    /// <summary>The browser's storage of the origin: an estimate, persist(), the marker, the databases and the Origin Private File System.</summary>
    private sealed class FakeOriginStorage : IOriginStorage
    {
        public OriginStorageStatus? Status { get; set; } = new(0, 0, false);

        public bool Persisted
        {
            get => Status?.IsPersisted == true;
            set => Status = (Status ?? new OriginStorageStatus(0, 0, false)) with { IsPersisted = value };
        }

        public int PersistCalls { get; private set; }

        public string? Marker { get; set; }

        public bool? DatabaseExists { get; set; } = false;

        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        public ValueTask<OriginStorageStatus?> GetStatusAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Status);

        public ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default)
        {
            PersistCalls++;
            Persisted = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask<string?> ReadBootMarkerAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Marker);

        public ValueTask WriteBootMarkerAsync(string marker, CancellationToken cancellationToken = default)
        {
            Marker = marker;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool?> DatabaseExistsAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult(DatabaseExists);

        public ValueTask<bool?> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<bool?>(Files.Contains(path));
    }
}
