
namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>The start of a Craft app in a window: loading, the app, or the error screen with "Retry".</summary>
public sealed class FrameStartupTests
{
    [Fact]
    public void A_new_window_shows_the_loading_screen()
    {
        FrameStartup startup = new();

        Assert.Equal(FrameState.Loading, startup.State);
        Assert.Equal(1, startup.Attempt);
    }

    [Fact]
    public void Ready_shows_WordCraft_with_its_version()
    {
        FrameStartup startup = new();

        Assert.True(startup.OnReady("0.18.1"));

        Assert.Equal(FrameState.Running, startup.State);
        Assert.Equal("0.18.1", startup.AppVersion);
    }

    [Fact]
    public void Failed_shows_the_error_WordCraft_reported()
    {
        FrameStartup startup = new();

        Assert.True(startup.OnFailed("no WebGPU nor WebGL2"));

        Assert.Equal(FrameState.Failed, startup.State);
        Assert.Equal("no WebGPU nor WebGL2", startup.Error);
        Assert.False(startup.TimedOut);
    }

    [Fact]
    public void No_answer_in_time_shows_the_timeout()
    {
        FrameStartup startup = new();

        Assert.True(startup.OnTimeout(startup.Attempt));

        Assert.Equal(FrameState.Failed, startup.State);
        Assert.True(startup.TimedOut);
        Assert.Null(startup.Error);
    }

    [Fact]
    public void The_timeout_waits_one_minute()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), FrameStartup.Timeout);
    }

    [Fact]
    public void A_timeout_after_ready_changes_nothing()
    {
        FrameStartup startup = new();
        startup.OnReady("0.18.1");

        Assert.False(startup.OnTimeout(startup.Attempt));
        Assert.Equal(FrameState.Running, startup.State);
    }

    [Fact]
    public void A_late_ready_after_the_timeout_still_shows_WordCraft()
    {
        FrameStartup startup = new();
        startup.OnTimeout(startup.Attempt);

        Assert.True(startup.OnReady("0.18.1"));

        Assert.Equal(FrameState.Running, startup.State);
        Assert.False(startup.TimedOut);
    }

    [Fact]
    public void Ready_after_a_reported_failure_is_ignored()
    {
        FrameStartup startup = new();
        startup.OnFailed("wasm not found");

        Assert.False(startup.OnReady("0.18.1"));
        Assert.Equal(FrameState.Failed, startup.State);
    }

    [Fact]
    public void A_failure_once_running_is_ignored()
    {
        FrameStartup startup = new();
        startup.OnReady("0.18.1");

        Assert.False(startup.OnFailed("late"));
        Assert.Equal(FrameState.Running, startup.State);
    }

    [Fact]
    public void Retry_starts_a_new_attempt_on_the_loading_screen()
    {
        FrameStartup startup = new();
        startup.OnFailed("wasm not found");

        int attempt = startup.Retry();

        Assert.Equal(2, attempt);
        Assert.Equal(FrameState.Loading, startup.State);
        Assert.Null(startup.Error);
    }

    [Fact]
    public void The_timeout_of_an_earlier_attempt_does_nothing()
    {
        FrameStartup startup = new();
        int first = startup.Attempt;
        startup.OnTimeout(first);
        startup.Retry();

        Assert.False(startup.OnTimeout(first));
        Assert.Equal(FrameState.Loading, startup.State);
    }
}
