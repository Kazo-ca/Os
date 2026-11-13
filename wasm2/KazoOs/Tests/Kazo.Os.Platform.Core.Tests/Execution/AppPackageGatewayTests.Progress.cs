using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Execution;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// ADR 0266 §7: an install reports its steps in the order the OS runs them, so the Store can show
/// them instead of a bare « Working… ».
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    [Fact]
    public async Task A_personal_install_reports_preparing_downloading_verifying_then_installing()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        RecordingProgress progress = new();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        await gateway.InstallPackageAsync(Trusted, KazoPackageScope.User, progress);

        Assert.Equal(
            [KazoPackageInstallStage.Preparing, KazoPackageInstallStage.Downloading, KazoPackageInstallStage.Verifying, KazoPackageInstallStage.Installing],
            progress.Stages);
        KazoPackageInstallProgress downloaded = progress.Reports.Last(report => report.Stage == KazoPackageInstallStage.Downloading);
        Assert.True(downloaded.BytesReceived > 0);
        Assert.Equal(downloaded.BytesReceived, downloaded.TotalBytes);
        Assert.Equal(1, downloaded.Fraction);
    }

    [Fact]
    public async Task A_system_install_reports_the_wait_for_the_administrator_before_installing()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        RecordingProgress progress = new();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(
            new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        await gateway.InstallPackageAsync(Trusted, KazoPackageScope.System, progress);

        Assert.Equal(
            [
                KazoPackageInstallStage.Preparing,
                KazoPackageInstallStage.Downloading,
                KazoPackageInstallStage.Verifying,
                KazoPackageInstallStage.AwaitingApproval,
                KazoPackageInstallStage.Installing
            ],
            progress.Stages);
    }

    [Fact]
    public async Task A_refused_package_stops_after_verifying_and_never_reports_installing()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0", corruptIndexHash: true);
        RecordingProgress progress = new();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        await Assert.ThrowsAsync<KazoPackageException>(() => gateway.InstallPackageAsync(Trusted, KazoPackageScope.User, progress).AsTask());

        Assert.Equal(KazoPackageInstallStage.Verifying, progress.Stages[^1]);
        Assert.DoesNotContain(KazoPackageInstallStage.Installing, progress.Stages);
    }

    [Fact]
    public async Task The_overload_without_progress_still_installs()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        KazoPackageInstallReport report = await gateway.InstallPackageAsync(Trusted, KazoPackageScope.User, progress: null);

        Assert.Equal("1.0.0", report.Version);
    }

    /// <summary>Keeps every report, synchronously, in order.</summary>
    private sealed class RecordingProgress : IProgress<KazoPackageInstallProgress>
    {
        public List<KazoPackageInstallProgress> Reports { get; } = [];

        /// <summary>The stages, each once, in the order they started.</summary>
        public List<KazoPackageInstallStage> Stages =>
            [.. Reports.Select(report => report.Stage).Where((stage, index) => index == 0 || Reports[index - 1].Stage != stage)];

        public void Report(KazoPackageInstallProgress value) => Reports.Add(value);
    }
}
