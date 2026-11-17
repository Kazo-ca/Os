using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Storage;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Storage;

/// <summary>Low-space warning and root reserve of ADR 0099, section 2.</summary>
public sealed class StorageSpaceMonitorTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public async Task Low_space_warns_the_session_user_once_with_an_action_that_opens_diagnostic()
    {
        Probe probe = new(new StorageCapacity(UsageBytes: 950 * MiB, QuotaBytes: 1000 * MiB));
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        await using StorageSpaceMonitor monitor = new(probe, queue, () => alice);

        await monitor.RefreshAsync();
        await monitor.RefreshAsync();

        Assert.True(monitor.IsLowSpace);
        Notification warning = Assert.Single(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
        Assert.Equal(NotificationSeverity.Warning, warning.Severity);
        NotificationAction action = Assert.Single(warning.Actions);
        Assert.True(action.TryGetLaunchedApp(out string? appId));
        Assert.Equal(StorageSpaceMonitor.DiagnosticAppId, appId);
    }

    [Fact]
    public async Task The_warning_is_written_in_the_session_language()
    {
        Probe probe = new(new StorageCapacity(UsageBytes: 950 * MiB, QuotaBytes: 1000 * MiB));
        InMemoryNotificationQueue english = new(maxEntriesPerUser: 10);
        InMemoryNotificationQueue french = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        await using StorageSpaceMonitor englishMonitor = new(probe, english, () => alice);
        await using StorageSpaceMonitor frenchMonitor = new(
            probe, french, () => alice, language: () => System.Globalization.CultureInfo.GetCultureInfo("fr"));

        await englishMonitor.RefreshAsync();
        await frenchMonitor.RefreshAsync();

        Notification inEnglish = Assert.Single(english.GetActive(alice.UserId, DateTimeOffset.UtcNow));
        Assert.Equal("Low storage space", inEnglish.Title);
        Assert.StartsWith("Only 50 MiB left of 1000 MiB.", inEnglish.Message, StringComparison.Ordinal);
        Notification inFrench = Assert.Single(french.GetActive(alice.UserId, DateTimeOffset.UtcNow));
        Assert.Equal("Espace de stockage faible", inFrench.Title);
        Assert.StartsWith("Plus que 50 Mio libres sur 1000 Mio.", inFrench.Message, StringComparison.Ordinal);
        Assert.Equal("Ouvrir Diagnostic", Assert.Single(inFrench.Actions).Label);
    }

    [Fact]
    public async Task A_new_session_user_is_warned_again()
    {
        Probe probe = new(new StorageCapacity(950 * MiB, 1000 * MiB));
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal current = Principal("alice");
        await using StorageSpaceMonitor monitor = new(probe, queue, () => current);

        await monitor.RefreshAsync();
        current = Principal("bob");
        await monitor.RefreshAsync();

        Assert.Single(queue.GetActive(current.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_root_reserve_is_five_percent_of_the_quota_at_most_256_mebibytes()
    {
        Probe probe = new(new StorageCapacity(940 * MiB, 1000 * MiB));
        await using StorageSpaceMonitor monitor = new(probe);

        await monitor.RefreshAsync();
        Assert.False(monitor.IsRootReserveReached);
        Assert.True(monitor.IsLowSpace);

        probe.Capacity = new StorageCapacity(951 * MiB, 1000 * MiB);
        await monitor.RefreshAsync();
        Assert.True(monitor.IsRootReserveReached);

        // A 100 GiB quota keeps 256 MiB for root, not 5 GiB.
        probe.Capacity = new StorageCapacity(100L * 1024 * MiB - 300 * MiB, 100L * 1024 * MiB);
        await monitor.RefreshAsync();
        Assert.False(monitor.IsRootReserveReached);
        Assert.Equal(256 * MiB, StorageSpacePolicy.ReserveBytes(100L * 1024 * MiB));
    }

    [Fact]
    public async Task An_unknown_quota_reserves_nothing_and_warns_nobody()
    {
        Probe probe = new(null);
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        await using StorageSpaceMonitor monitor = new(probe, queue, () => alice);

        await monitor.RefreshAsync();

        Assert.False(monitor.IsRootReserveReached);
        Assert.False(monitor.IsLowSpace);
        Assert.Null(monitor.LastCapacity);
        Assert.Empty(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Becoming_low_reclaims_orphans_first_and_warns_nobody_when_that_is_enough()
    {
        Probe probe = new(new StorageCapacity(950 * MiB, 1000 * MiB));
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        Reclaimer reclaimer = new(() => probe.Capacity = new StorageCapacity(500 * MiB, 1000 * MiB));
        await using StorageSpaceMonitor monitor = new(probe, queue, () => alice) { Reclaimer = reclaimer };

        await monitor.RefreshAsync();

        Assert.Equal([true], reclaimer.Calls);
        Assert.False(monitor.IsLowSpace);
        Assert.Empty(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Becoming_low_warns_once_when_reclaiming_is_not_enough()
    {
        Probe probe = new(new StorageCapacity(950 * MiB, 1000 * MiB));
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        Reclaimer reclaimer = new(() => { });
        await using StorageSpaceMonitor monitor = new(probe, queue, () => alice) { Reclaimer = reclaimer };

        await monitor.RefreshAsync();
        await monitor.RefreshAsync();

        // Reclaimed once when the storage became low, not again while it stays low.
        Assert.Equal([true], reclaimer.Calls);
        Assert.True(monitor.IsLowSpace);
        Assert.Single(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_failing_reclaimer_does_not_fail_the_measure()
    {
        Probe probe = new(new StorageCapacity(950 * MiB, 1000 * MiB));
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        Reclaimer reclaimer = new(() => throw new InvalidOperationException("store closed"));
        await using StorageSpaceMonitor monitor = new(probe, queue, () => alice) { Reclaimer = reclaimer };

        await monitor.RefreshAsync();

        Assert.True(monitor.IsLowSpace);
        Assert.Single(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Plenty_of_space_never_reclaims_on_a_measure()
    {
        Probe probe = new(new StorageCapacity(100 * MiB, 1000 * MiB));
        Reclaimer reclaimer = new(() => { });
        await using StorageSpaceMonitor monitor = new(probe) { Reclaimer = reclaimer };

        await monitor.RefreshAsync();

        Assert.Empty(reclaimer.Calls);
    }

    [Fact]
    public async Task A_burst_of_reclaim_requests_runs_one_reclaim()
    {
        Probe probe = new(new StorageCapacity(100 * MiB, 1000 * MiB));
        Reclaimer reclaimer = new(() => { });
        await using StorageSpaceMonitor monitor = new(probe) { Reclaimer = reclaimer };

        // Emptying a trash of three elements: three deletions, one reclaim (ADR 0273 section 4).
        monitor.RequestReclaim();
        monitor.RequestReclaim();
        monitor.RequestReclaim();
        await reclaimer.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(StorageSpaceMonitor.ReclaimRequestDelay);

        Assert.Equal([false], reclaimer.Calls);
    }

    private sealed class Reclaimer(Action onReclaim) : IStorageReclaimer
    {
        public List<bool> Calls { get; } = [];

        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<long> ReclaimAsync(bool lowSpace, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add(lowSpace);
            }

            onReclaim();
            FirstCall.TrySetResult();
            return ValueTask.FromResult(1L);
        }
    }

    private static AuthenticatedPrincipal Principal(string login)
    {
        LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse(login),
            login,
            AppAuthority.User,
            group,
            [group],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task A_write_refused_by_the_browser_warns_even_when_the_rounded_measure_looks_roomy()
    {
        // Chromium rounds navigator.storage.estimate(): 4 GiB "quota", almost nothing used (ADR 0279).
        Probe probe = new(new StorageCapacity(UsageBytes: 1 * MiB, QuotaBytes: 4096 * MiB));
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        int reclaims = 0;
        await using StorageSpaceMonitor monitor = new(probe, queue, () => alice)
        {
            Reclaimer = new Reclaimer(() => Interlocked.Increment(ref reclaims))
        };

        monitor.ReportWriteRefused();
        Notification warning = await WaitForSingleAsync(queue, alice);

        Assert.True(monitor.IsLowSpace);
        Assert.False(monitor.IsRootReserveReached);
        Assert.Equal("Low storage space", warning.Title);
        Assert.StartsWith("The browser refused to save data", warning.Message, StringComparison.Ordinal);
        Assert.True(Volatile.Read(ref reclaims) >= 1);

        // Once per session user, like the measured warning.
        monitor.ReportWriteRefused();
        await monitor.RefreshAsync();
        Assert.Single(queue.GetActive(alice.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_refused_write_holds_the_storage_low_for_ten_minutes_then_the_measure_decides()
    {
        Probe probe = new(new StorageCapacity(UsageBytes: 1 * MiB, QuotaBytes: 4096 * MiB));
        ManualTime time = new();
        await using StorageSpaceMonitor monitor = new(probe, time: time);

        monitor.ReportWriteRefused();
        await monitor.RefreshAsync();
        Assert.True(monitor.IsLowSpace);

        time.Advance(StorageSpaceMonitor.RefusalHoldTime);
        await monitor.RefreshAsync();
        Assert.False(monitor.IsLowSpace);
    }

    [Fact]
    public async Task A_refused_write_is_written_in_the_session_language()
    {
        Probe probe = new(null);
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        AuthenticatedPrincipal alice = Principal("alice");
        await using StorageSpaceMonitor monitor = new(
            probe, queue, () => alice, language: () => System.Globalization.CultureInfo.GetCultureInfo("fr"));

        monitor.ReportWriteRefused();
        Notification warning = await WaitForSingleAsync(queue, alice);

        Assert.Equal("Espace de stockage faible", warning.Title);
        Assert.StartsWith("Le navigateur a refusé d'enregistrer des données", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>Waits for the one warning <see cref="StorageSpaceMonitor.ReportWriteRefused"/> posts in the background.</summary>
    private static async Task<Notification> WaitForSingleAsync(InMemoryNotificationQueue queue, AuthenticatedPrincipal user)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.GetActive(user.UserId, DateTimeOffset.UtcNow).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return Assert.Single(queue.GetActive(user.UserId, DateTimeOffset.UtcNow));
    }

    /// <summary>A clock the test moves by hand (timestamps in ticks).</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class Probe(StorageCapacity? capacity) : IStorageCapacityProbe
    {
        public StorageCapacity? Capacity { get; set; } = capacity;

        public ValueTask<StorageCapacity?> GetCapacityAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Capacity);
    }
}
