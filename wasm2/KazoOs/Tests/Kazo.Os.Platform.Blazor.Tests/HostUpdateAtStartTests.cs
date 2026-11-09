using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Kazo.Os.Platform.Core.Lifecycle;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>ADR 0239: Kazo OS and the apps it opens start only on the version the origin serves.</summary>
public sealed class HostUpdateAtStartTests
{
    private const string Running = "3.2.1";

    [Fact]
    public async Task The_boot_updates_when_a_newer_version_is_served()
    {
        HostReleaseMonitor monitor = new(new CountingProbe("""{ "version": "3.3.0", "build": 812 }"""), Running, 800);
        List<string> targets = [];

        bool update = await new HostBootUpdateCheck(monitor, Claim(targets)).ShouldUpdateAsync();

        Assert.True(update);
        Assert.Equal(["3.3.0+812"], targets);
    }

    [Fact]
    public async Task The_boot_does_not_update_twice_to_the_same_version_in_a_tab()
    {
        HostReleaseMonitor monitor = new(new CountingProbe("""{ "version": "3.3.0", "build": 812 }"""), Running, 800);
        HashSet<string> tried = [];
        HostBootUpdateCheck check = new(monitor, target => ValueTask.FromResult(tried.Add(target)));

        Assert.True(await check.ShouldUpdateAsync());
        Assert.False(await check.ShouldUpdateAsync());
    }

    [Theory]
    [InlineData("""{ "version": "3.2.1", "build": 800 }""")]
    [InlineData(null)]
    public async Task The_boot_starts_on_the_served_version_or_offline(string? document)
    {
        HostReleaseMonitor monitor = new(new CountingProbe(document), Running, 800);
        List<string> targets = [];

        Assert.False(await new HostBootUpdateCheck(monitor, Claim(targets)).ShouldUpdateAsync());
        Assert.Empty(targets);
    }

    [Fact]
    public async Task A_slow_origin_does_not_hold_the_boot()
    {
        HostReleaseMonitor monitor = new(new HangingProbe(), Running, 800);

        bool update = await new HostBootUpdateCheck(monitor, Claim([]), TimeSpan.FromMilliseconds(50)).ShouldUpdateAsync();

        Assert.False(update);
    }

    [Fact]
    public async Task An_app_opens_on_the_served_version()
    {
        HostUpdateLaunchGate gate = CreateGate(new CountingProbe("""{ "version": "3.2.1", "build": 800 }"""), out _);

        Assert.Null(await gate.CheckAsync(Manifest(), CancellationToken.None));
    }

    [Fact]
    public async Task An_app_does_not_open_on_an_outdated_copy_and_the_restart_is_offered()
    {
        HostUpdateLaunchGate gate = CreateGate(new CountingProbe("""{ "version": "3.2.1", "build": 805 }"""), out _);
        List<string> offered = [];
        gate.UpdateRequired += offered.Add;

        AppLaunchRefusal? refusal = await gate.CheckAsync(Manifest(), CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Equal(AppLaunchRefusal.UpdateRequiredCode, refusal.ErrorCode);
        Assert.Equal("A new version of Kazo OS is available. Restart to open \"IRC\".", refusal.Message);
        Assert.Equal(["IRC"], offered);
    }

    [Fact]
    public async Task The_origin_is_read_again_only_after_the_recheck_interval()
    {
        ManualTime time = new();
        CountingProbe probe = new("""{ "version": "3.2.1", "build": 800 }""");
        HostUpdateLaunchGate gate = CreateGate(probe, out _, time);

        await gate.CheckAsync(Manifest(), CancellationToken.None);
        await gate.CheckAsync(Manifest(), CancellationToken.None);
        Assert.Equal(1, probe.Reads);

        time.Advance(HostUpdateLaunchGate.RecheckInterval);
        probe.Document = """{ "version": "3.3.0", "build": 812 }""";

        Assert.NotNull(await gate.CheckAsync(Manifest(), CancellationToken.None));
        Assert.Equal(2, probe.Reads);
    }

    [Fact]
    public async Task An_unreachable_origin_lets_the_app_open()
    {
        HostUpdateLaunchGate gate = CreateGate(new CountingProbe(null), out _);

        Assert.Null(await gate.CheckAsync(Manifest(), CancellationToken.None));
    }

    private static HostUpdateLaunchGate CreateGate(IHostReleaseProbe probe, out HostReleaseMonitor monitor, TimeProvider? time = null)
    {
        monitor = new HostReleaseMonitor(probe, Running, 800);
        return new HostUpdateLaunchGate(monitor, () => CultureInfo.InvariantCulture, time);
    }

    private static Func<string, ValueTask<bool>> Claim(List<string> targets) => target =>
    {
        targets.Add(target);
        return ValueTask.FromResult(true);
    };

    private static AppManifest Manifest() => new()
    {
        Id = "ca.kazo.irc",
        Name = "IRC",
        Version = "1.0.0",
        PublisherId = "ca.kazo",
        Description = "Launch gate fixture.",
        Kind = AppKind.Window,
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("internet", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
    };

    private sealed class CountingProbe(string? document) : IHostReleaseProbe
    {
        public string? Document { get; set; } = document;

        public int Reads { get; private set; }

        public ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Document);
        }
    }

    private sealed class HangingProbe : IHostReleaseProbe
    {
        public async ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
