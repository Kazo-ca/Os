using Kazo.Os.Platform.Blazor.LazyLoading;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>ADR 0174: the OS reads the version.json its origin serves and reports a newer release.</summary>
public sealed class HostReleaseMonitorTests
{
    private const string Running = "2.1.0";

    [Theory]
    [InlineData("""{ "product": "Kazo OS", "version": "2.2.0" }""", "2.2.0")]
    [InlineData("""{ "version": " 3.0.0-preview.1 " }""", "3.0.0-preview.1")]
    [InlineData("""{ "product": "Kazo OS" }""", null)]
    [InlineData("""{ "version": 2 }""", null)]
    [InlineData("""[ "2.2.0" ]""", null)]
    [InlineData("<!DOCTYPE html>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_version_is_the_version_string_of_the_document(string? document, string? expected) =>
        Assert.Equal(expected, HostReleaseMonitor.ParseVersion(document));

    [Fact]
    public async Task Another_served_version_is_reported_once_per_version()
    {
        var probe = new SequenceProbe("""{ "version": "2.2.0" }""");
        var monitor = new HostReleaseMonitor(probe, Running);
        List<string> reported = [];
        monitor.ReleaseAvailable += reported.Add;

        Assert.True(await monitor.CheckAsync());
        Assert.True(await monitor.CheckAsync());
        probe.Document = """{ "version": "2.3.0" }""";
        Assert.True(await monitor.CheckAsync());

        Assert.Equal(["2.2.0", "2.3.0"], reported);
        Assert.Equal("2.3.0", monitor.AvailableVersion);
    }

    [Theory]
    [InlineData("""{ "version": "2.1.0" }""")]
    [InlineData(null)]
    [InlineData("not json")]
    public async Task The_running_version_or_an_unreadable_file_is_not_an_update(string? document)
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe(document), Running);
        monitor.ReleaseAvailable += _ => Assert.Fail("No release expected.");

        Assert.False(await monitor.CheckAsync());
        Assert.Null(monitor.AvailableVersion);
    }

    [Fact]
    public async Task A_probe_that_throws_is_not_an_update()
    {
        var monitor = new HostReleaseMonitor(new ThrowingProbe(), Running);

        Assert.False(await monitor.CheckAsync());
    }

    [Fact]
    public async Task Each_check_also_asks_whether_the_build_changed_under_the_page()
    {
        var buildMonitor = new HostBuildUpdateMonitor(new FixedBuildProbe("_framework/dotnet.5kv7vq3e4n.js", "_framework/dotnet.bdig46o9on.js"));
        var monitor = new HostReleaseMonitor(new SequenceProbe("""{ "version": "2.1.0" }"""), Running, buildMonitor);
        monitor.ReleaseAvailable += _ => Assert.Fail("The version did not change.");

        await monitor.CheckAsync();

        Assert.True(buildMonitor.IsUpdatePending);
    }

    [Fact]
    public async Task Started_it_checks_at_each_interval_until_disposed()
    {
        var probe = new SequenceProbe("""{ "version": "2.2.0" }""");
        await using var monitor = new HostReleaseMonitor(probe, Running);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ReleaseAvailable += version => reported.TrySetResult(version);

        monitor.Start(TimeSpan.FromMilliseconds(20));
        monitor.Start(TimeSpan.FromMilliseconds(20));

        Assert.Equal("2.2.0", await reported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void The_interval_must_be_positive_and_the_running_version_known()
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe(null), Running);

        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Start(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new HostReleaseMonitor(new SequenceProbe(null), " "));
        Assert.Equal(TimeSpan.FromMinutes(5), HostReleaseMonitor.DefaultInterval);
    }

    [Theory]
    [InlineData("""{ "version": "3.2.1", "build": 1234 }""", 1234)]
    [InlineData("""{ "version": "3.2.1", "build": "1234" }""", 1234)]
    [InlineData("""{ "version": "3.2.1", "build": 0 }""", null)]
    [InlineData("""{ "version": "3.2.1", "build": -4 }""", null)]
    [InlineData("""{ "version": "3.2.1", "build": "dev" }""", null)]
    [InlineData("""{ "version": "3.2.1" }""", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void The_build_is_a_positive_number_of_the_document(string? document, int? expected) =>
        Assert.Equal(expected, HostReleaseMonitor.ParseBuild(document));

    [Fact]
    public void Before_the_first_check_the_status_is_not_checked()
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe(null), Running, runningBuild: 12);

        Assert.Equal(HostReleaseStatus.NotChecked, monitor.Status);
        Assert.Equal(12, monitor.RunningBuild);
    }

    [Theory]
    [InlineData("""{ "version": "2.1.0", "build": 12 }""", 12)]
    [InlineData("""{ "version": "2.1.0" }""", 12)]
    [InlineData("""{ "version": "2.1.0", "build": 12 }""", null)]
    public async Task The_running_version_and_build_are_up_to_date(string document, int? runningBuild)
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe(document), Running, runningBuild);
        int changes = 0;
        monitor.StatusChanged += () => changes++;

        Assert.False(await monitor.CheckAsync());
        Assert.False(await monitor.CheckAsync());

        Assert.Equal(HostReleaseStatus.UpToDate, monitor.Status);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task A_newer_build_of_the_same_version_is_an_update_without_a_new_version()
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe("""{ "version": "2.1.0", "build": 15 }"""), Running, runningBuild: 12);
        monitor.ReleaseAvailable += _ => Assert.Fail("The version did not change.");

        Assert.True(await monitor.CheckAsync());

        Assert.Equal(HostReleaseStatus.UpdateAvailable, monitor.Status);
        Assert.Null(monitor.AvailableVersion);
        Assert.Equal(15, monitor.AvailableBuild);
    }

    [Fact]
    public async Task Another_version_reports_its_build_with_it()
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe("""{ "version": "2.2.0", "build": 20 }"""), Running, runningBuild: 12);

        Assert.True(await monitor.CheckAsync());

        Assert.Equal(HostReleaseStatus.UpdateAvailable, monitor.Status);
        Assert.Equal("2.2.0", monitor.AvailableVersion);
        Assert.Equal(20, monitor.AvailableBuild);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    public async Task An_unreadable_origin_leaves_the_status_unreachable(string? document)
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe(document), Running);

        Assert.False(await monitor.CheckAsync());

        Assert.Equal(HostReleaseStatus.Unreachable, monitor.Status);
    }

    [Fact]
    public async Task A_probe_that_throws_leaves_the_status_unreachable()
    {
        var monitor = new HostReleaseMonitor(new ThrowingProbe(), Running);

        Assert.False(await monitor.CheckAsync());

        Assert.Equal(HostReleaseStatus.Unreachable, monitor.Status);
    }

    [Fact]
    public async Task Once_outdated_the_page_stays_outdated_when_the_origin_goes_offline()
    {
        var probe = new SequenceProbe("""{ "version": "2.2.0" }""");
        var monitor = new HostReleaseMonitor(probe, Running);

        Assert.True(await monitor.CheckAsync());
        probe.Document = null;
        Assert.True(await monitor.CheckAsync());

        Assert.Equal(HostReleaseStatus.UpdateAvailable, monitor.Status);
    }

    [Fact]
    public async Task A_build_changed_under_the_page_makes_it_outdated()
    {
        var buildMonitor = new HostBuildUpdateMonitor(new FixedBuildProbe("_framework/dotnet.5kv7vq3e4n.js", "_framework/dotnet.bdig46o9on.js"));
        var monitor = new HostReleaseMonitor(new SequenceProbe("""{ "version": "2.1.0" }"""), Running, runningBuild: null, buildMonitor);

        Assert.True(await monitor.CheckAsync());

        Assert.Equal(HostReleaseStatus.UpdateAvailable, monitor.Status);
        Assert.Null(monitor.AvailableVersion);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("dev", null)]
    [InlineData("0", null)]
    [InlineData("1234", 1234)]
    public void The_running_build_comes_from_the_assembly_metadata(string? value, int? expected) =>
        Assert.Equal(expected, HostBuildNumber.Parse(value));

    [Fact]
    public void An_assembly_without_the_metadata_has_no_build_number()
    {
        Assert.Null(HostBuildNumber.Read(typeof(HostReleaseMonitorTests).Assembly));
        Assert.Null(HostBuildNumber.Read(null));
    }

    private sealed class SequenceProbe(string? document) : IHostReleaseProbe
    {
        public string? Document { get; set; } = document;

        public ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Document);
    }

    private sealed class ThrowingProbe : IHostReleaseProbe
    {
        public ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }

    private sealed class FixedBuildProbe(string? running, string? served) : IHostBuildProbe
    {
        public ValueTask<string?> GetRunningBuildAsync(CancellationToken cancellationToken) => ValueTask.FromResult(running);
        public ValueTask<string?> GetServedBuildAsync(CancellationToken cancellationToken) => ValueTask.FromResult(served);
    }
}
