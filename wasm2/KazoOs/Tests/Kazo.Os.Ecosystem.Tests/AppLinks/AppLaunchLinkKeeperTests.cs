using Kazo.Os.Ecosystem.AppLinks;
using Kazo.Os.Platform.Core.AppLinks;

namespace Kazo.Os.Ecosystem.Tests.AppLinks;

/// <summary>
/// ADR 0265: the direct link survives a reload of the tab (a restart for an update) until it has
/// finished; the address wins over what the tab kept.
/// </summary>
public sealed class AppLaunchLinkKeeperTests
{
    [Fact]
    public async Task A_link_of_the_address_is_run_and_kept_by_the_tab()
    {
        MemoryStore store = new();
        AppLaunchLinkKeeper keeper = new(store);

        AppLaunchLink? link = await keeper.ReadAtBootAsync("https://os.kazo.ca/?autolaunch=ca.kazo.word");

        Assert.Equal("ca.kazo.word", link?.AppId);
        Assert.Equal("ca.kazo.word", store.Value);
    }

    [Fact]
    public async Task A_reload_without_the_parameter_runs_the_link_the_tab_kept()
    {
        MemoryStore store = new() { Value = "ca.kazo.word" };
        AppLaunchLinkKeeper keeper = new(store);

        // The restart for an update reloads an address the first boot already cleaned.
        AppLaunchLink? link = await keeper.ReadAtBootAsync("https://os.kazo.ca/");

        Assert.Equal("ca.kazo.word", link?.AppId);
        Assert.Equal("ca.kazo.word", store.Value);
    }

    [Fact]
    public async Task The_address_wins_over_the_link_the_tab_kept()
    {
        MemoryStore store = new() { Value = "ca.kazo.word" };
        AppLaunchLinkKeeper keeper = new(store);

        AppLaunchLink? link = await keeper.ReadAtBootAsync("https://os.kazo.ca/?autolaunch=ca.kazo.sheets");

        Assert.Equal("ca.kazo.sheets", link?.AppId);
        Assert.Equal("ca.kazo.sheets", store.Value);
    }

    [Fact]
    public async Task A_malformed_kept_value_is_dropped()
    {
        MemoryStore store = new() { Value = "https://evil.example/app.kazopkg" };
        AppLaunchLinkKeeper keeper = new(store);

        Assert.Null(await keeper.ReadAtBootAsync("https://os.kazo.ca/"));
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task Nothing_runs_once_the_link_has_finished()
    {
        MemoryStore store = new();
        AppLaunchLinkKeeper keeper = new(store);
        await keeper.ReadAtBootAsync("https://os.kazo.ca/?autolaunch=ca.kazo.word");

        await keeper.FinishAsync();

        Assert.Null(store.Value);
        Assert.Null(await keeper.ReadAtBootAsync("https://os.kazo.ca/"));
    }

    [Fact]
    public async Task Without_a_store_only_the_address_counts()
    {
        AppLaunchLinkKeeper keeper = new(store: null);

        Assert.Equal("ca.kazo.word", (await keeper.ReadAtBootAsync("/?autolaunch=ca.kazo.word"))?.AppId);
        Assert.Null(await keeper.ReadAtBootAsync("/"));
        await keeper.FinishAsync();
    }

    private sealed class MemoryStore : IAppLaunchLinkStore
    {
        public string? Value { get; set; }

        public ValueTask<string?> ReadAsync() => ValueTask.FromResult(Value);

        public ValueTask SaveAsync(string appId)
        {
            Value = appId;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync()
        {
            Value = null;
            return ValueTask.CompletedTask;
        }
    }
}
