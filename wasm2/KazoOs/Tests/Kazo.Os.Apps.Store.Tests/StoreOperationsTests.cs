using Kazo.Os.AppSdk.Apps;
using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>
/// ADR 0266 §7: the Store follows each app's action on its own, shows the steps the OS reports, and
/// runs the installs one at a time.
/// </summary>
public sealed class StoreOperationsTests
{
    [Fact]
    public async Task Only_the_app_being_installed_is_busy()
    {
        StoreOperations operations = new(() => { });
        TaskCompletionSource release = new();

        Task<bool> install = operations.RunAsync("org.example.notes", StoreOperationStage.Preparing, exclusive: true, () => release.Task);

        Assert.True(operations.IsRunning("org.example.notes"));
        Assert.False(operations.IsRunning("org.example.paint"));
        Assert.True(operations.Any);
        Assert.Equal(StoreOperationStage.Preparing, operations.Find("org.example.notes")!.Stage);

        release.SetResult();
        Assert.True(await install);
        Assert.False(operations.Any);
        Assert.Null(operations.Find("org.example.notes"));
    }

    [Fact]
    public async Task A_second_install_waits_its_turn_and_shows_it()
    {
        StoreOperations operations = new(() => { });
        TaskCompletionSource firstRelease = new();
        List<string> order = [];

        Task<bool> first = operations.RunAsync("org.example.notes", StoreOperationStage.Preparing, exclusive: true, async () =>
        {
            order.Add("notes");
            await firstRelease.Task;
        });
        Task<bool> second = operations.RunAsync("org.example.paint", StoreOperationStage.Preparing, exclusive: true, () =>
        {
            order.Add("paint");
            return Task.CompletedTask;
        });

        Assert.Equal(StoreOperationStage.Queued, operations.Find("org.example.paint")!.Stage);
        Assert.Equal(["notes"], order);

        firstRelease.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(["notes", "paint"], order);
        Assert.False(operations.Any);
    }

    [Fact]
    public async Task A_launch_does_not_wait_for_an_install()
    {
        StoreOperations operations = new(() => { });
        TaskCompletionSource release = new();
        Task<bool> install = operations.RunAsync("org.example.notes", StoreOperationStage.Preparing, exclusive: true, () => release.Task);

        bool launched = await operations.RunAsync("org.example.paint", StoreOperationStage.Working, exclusive: false, () => Task.CompletedTask);

        Assert.True(launched);
        Assert.True(operations.IsRunning("org.example.notes"));
        release.SetResult();
        await install;
    }

    [Fact]
    public async Task A_second_click_on_the_same_app_is_ignored()
    {
        StoreOperations operations = new(() => { });
        TaskCompletionSource release = new();
        int runs = 0;
        Task<bool> first = operations.RunAsync("org.example.notes", StoreOperationStage.Preparing, exclusive: true, () =>
        {
            runs++;
            return release.Task;
        });

        bool again = await operations.RunAsync("org.example.notes", StoreOperationStage.Preparing, exclusive: true, () =>
        {
            runs++;
            return Task.CompletedTask;
        });

        Assert.False(again);
        release.SetResult();
        await first;
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_failed_action_frees_the_app_and_the_next_install()
    {
        StoreOperations operations = new(() => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.RunAsync(
            "org.example.notes", StoreOperationStage.Preparing, exclusive: true, () => throw new InvalidOperationException()));

        Assert.False(operations.Any);
        Assert.True(await operations.RunAsync("org.example.paint", StoreOperationStage.Preparing, exclusive: true, () => Task.CompletedTask));
    }

    [Fact]
    public async Task The_steps_the_os_reports_replace_each_other_and_redraw()
    {
        int redraws = 0;
        StoreOperations operations = new(() => redraws++);
        TaskCompletionSource release = new();
        Task<bool> install = operations.RunAsync("org.example.notes", StoreOperationStage.Preparing, exclusive: true, () => release.Task);
        IProgress<KazoPackageInstallProgress> progress = operations.ProgressFor("org.example.notes", catalogBytes: 2048);
        int before = redraws;

        progress.Report(new KazoPackageInstallProgress(KazoPackageInstallStage.Downloading, 512, 1024));
        StoreOperation downloading = operations.Find("org.example.notes")!;
        progress.Report(new KazoPackageInstallProgress(KazoPackageInstallStage.Downloading, 512, 1024));
        progress.Report(new KazoPackageInstallProgress(KazoPackageInstallStage.AwaitingApproval));

        Assert.Equal((StoreOperationStage.Downloading, (int?)50), (downloading.Stage, downloading.Percent));
        Assert.Equal(StoreOperationStage.AwaitingApproval, operations.Find("org.example.notes")!.Stage);
        Assert.Null(operations.Find("org.example.notes")!.Percent);
        Assert.Equal(before + 2, redraws);
        release.SetResult();
        await install;
    }

    [Fact]
    public async Task A_host_without_a_size_falls_back_on_the_catalogs()
    {
        StoreOperations operations = new(() => { });
        TaskCompletionSource release = new();
        Task<bool> install = operations.RunAsync("org.example.notes", StoreOperationStage.Preparing, exclusive: true, () => release.Task);

        operations.ProgressFor("org.example.notes", catalogBytes: 4000)
            .Report(new KazoPackageInstallProgress(KazoPackageInstallStage.Downloading, 1000));

        StoreOperation operation = operations.Find("org.example.notes")!;
        Assert.Equal(((long?)1000, (long?)4000, (int?)25), (operation.BytesReceived, operation.TotalBytes, operation.Percent));
        release.SetResult();
        await install;
    }

    [Fact]
    public void A_report_after_the_end_changes_nothing()
    {
        StoreOperations operations = new(() => { });

        operations.ProgressFor("org.example.notes", null).Report(new KazoPackageInstallProgress(KazoPackageInstallStage.Installing));

        Assert.False(operations.Any);
    }

    [Theory]
    [InlineData(0L, 100L, 0)]
    [InlineData(99L, 100L, 99)]
    [InlineData(150L, 100L, 100)]
    public void The_percent_is_whole_and_never_passes_one_hundred(long received, long total, int expected)
    {
        Assert.Equal(expected, new StoreOperation("a", StoreOperationStage.Downloading, received, total).Percent);
    }

    [Fact]
    public void Outside_the_download_or_without_a_size_there_is_no_percent()
    {
        Assert.Null(new StoreOperation("a", StoreOperationStage.Verifying, 10, 100).Percent);
        Assert.Null(new StoreOperation("a", StoreOperationStage.Downloading, 10).Percent);
        Assert.Null(new StoreOperation("a", StoreOperationStage.Downloading, 10, 0).Percent);
    }

    [Fact]
    public void The_sdk_progress_gives_the_same_share()
    {
        Assert.Equal(0.25, new KazoPackageInstallProgress(KazoPackageInstallStage.Downloading, 1, 4).Fraction);
        Assert.Null(new KazoPackageInstallProgress(KazoPackageInstallStage.Downloading, 1).Fraction);
        Assert.Null(new KazoPackageInstallProgress(KazoPackageInstallStage.Installing, 4, 4).Fraction);
    }
}
