using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Shell;

public sealed class StartMenuPreferencesServiceTests
{
    [Fact]
    public async Task InitializeAsync_starts_each_user_with_an_empty_pin_list()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, []);

        await service.InitializeAsync();

        Assert.Empty(service.GetPinnedAppIds(alice.UserId));
        Assert.Empty(service.GetPinnedAppIds(harness.Bob.Id));
    }

    [Fact]
    public async Task PinAsync_persists_deduplicates_and_reloads_unknown_ids()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, []);
        await service.InitializeAsync();

        Assert.True(await service.PinAsync(alice.UserId, "org.example.unmounted"));
        Assert.False(await service.PinAsync(alice.UserId, "org.example.unmounted"));
        Assert.Equal(["org.example.unmounted"], service.GetPinnedAppIds(alice.UserId));

        // Pins are the user's own file (ADR 0044 section 8), not a shared machine document.
        Assert.Contains("org.example.unmounted", await harness.ReadAsync("/home/alice/.config/kazoos/start-menu.json"));

        using StartMenuPreferencesService reloaded = new(harness.Settings, harness.Session, []);
        await reloaded.InitializeAsync();
        Assert.Equal(["org.example.unmounted"], reloaded.GetPinnedAppIds(alice.UserId));
    }

    [Fact]
    public async Task MoveAsync_reorders_the_session_profile_and_never_touches_another_user()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, []);
        await service.InitializeAsync();
        await service.PinAsync(alice.UserId, "org.kazoos.browser");
        await service.PinAsync(alice.UserId, "org.kazoos.files");
        await service.PinAsync(alice.UserId, "org.kazoos.settings");

        Assert.False(await service.PinAsync(harness.Bob.Id, "org.example.private"));
        Assert.True(await service.MoveAsync(alice.UserId, "org.kazoos.settings", targetIndex: 0));

        Assert.Equal(
            ["org.kazoos.settings", "org.kazoos.browser", "org.kazoos.files"],
            service.GetPinnedAppIds(alice.UserId));
        Assert.Empty(service.GetPinnedAppIds(harness.Bob.Id));
        Assert.Null(await harness.ReadAsync("/home/bob/.config/kazoos/start-menu.json"));

        using StartMenuPreferencesService reloaded = new(harness.Settings, harness.Session, []);
        await reloaded.InitializeAsync();
        Assert.Equal(service.GetPinnedAppIds(alice.UserId), reloaded.GetPinnedAppIds(alice.UserId));
    }

    private static readonly string[] Store = ["org.kazoos.store"];

    [Fact]
    public async Task A_new_profile_gets_the_default_pins_and_records_them()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, ["org.example.welcome"]);

        await service.InitializeAsync();

        Assert.Equal(["org.example.welcome"], service.GetPinnedAppIds(alice.UserId));
        string? file = await harness.ReadAsync("/home/alice/.config/kazoos/start-menu.json");
        Assert.Contains("\"defaultPinsApplied\":[\"org.example.welcome\"]", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_profile_gets_a_default_pin_once_after_its_own_pins()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using (StartMenuPreferencesService before = new(harness.Settings, harness.Session, []))
        {
            await before.InitializeAsync();
            await before.PinAsync(alice.UserId, "org.kazoos.terminal");
        }

        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, ["org.example.welcome"]);
        await service.InitializeAsync();

        Assert.Equal(["org.kazoos.terminal", "org.example.welcome"], service.GetPinnedAppIds(alice.UserId));
    }

    [Fact]
    public async Task An_unpinned_default_pin_stays_unpinned()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using (StartMenuPreferencesService service = new(harness.Settings, harness.Session, ["org.example.welcome"]))
        {
            await service.InitializeAsync();
            Assert.True(await service.UnpinAsync(alice.UserId, "org.example.welcome"));
        }

        using StartMenuPreferencesService reloaded = new(harness.Settings, harness.Session, ["org.example.welcome"]);
        await reloaded.InitializeAsync();

        Assert.Empty(reloaded.GetPinnedAppIds(alice.UserId));
    }

    [Fact]
    public async Task A_new_profile_no_longer_gets_the_store_pinned()
    {
        // ADR 0268: the Store lives in the Start menu's Quick access panel.
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session);

        await service.InitializeAsync();

        Assert.Empty(service.GetPinnedAppIds(alice.UserId));
        Assert.DoesNotContain("org.kazoos.store", await harness.ReadAsync("/home/alice/.config/kazoos/start-menu.json") ?? string.Empty);
    }

    [Fact]
    public async Task A_store_pinned_by_default_is_removed_once_and_the_user_pins_stay()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using (StartMenuPreferencesService before = new(harness.Settings, harness.Session, Store))
        {
            await before.InitializeAsync();
            await before.PinAsync(alice.UserId, "org.kazoos.terminal");
            Assert.Equal(["org.kazoos.store", "org.kazoos.terminal"], before.GetPinnedAppIds(alice.UserId));
        }

        using StartMenuPreferencesService service = new(harness.Settings, harness.Session);
        await service.InitializeAsync();

        Assert.Equal(["org.kazoos.terminal"], service.GetPinnedAppIds(alice.UserId));
        string? file = await harness.ReadAsync("/home/alice/.config/kazoos/start-menu.json");
        Assert.DoesNotContain("org.kazoos.store", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_the_user_pins_again_after_the_migration_stays_pinned()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using (StartMenuPreferencesService before = new(harness.Settings, harness.Session, Store))
        {
            await before.InitializeAsync();
        }

        using (StartMenuPreferencesService migrated = new(harness.Settings, harness.Session))
        {
            await migrated.InitializeAsync();
            Assert.Empty(migrated.GetPinnedAppIds(alice.UserId));
            Assert.True(await migrated.PinAsync(alice.UserId, "org.kazoos.store"));
        }

        using StartMenuPreferencesService reloaded = new(harness.Settings, harness.Session);
        await reloaded.InitializeAsync();

        Assert.Equal(Store, reloaded.GetPinnedAppIds(alice.UserId));
    }

    [Fact]
    public async Task A_store_pinned_by_the_user_before_any_default_stays_pinned()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using (StartMenuPreferencesService before = new(harness.Settings, harness.Session, []))
        {
            await before.InitializeAsync();
            await before.PinAsync(alice.UserId, "org.kazoos.store");
        }

        using StartMenuPreferencesService service = new(harness.Settings, harness.Session);
        await service.InitializeAsync();

        Assert.Equal(Store, service.GetPinnedAppIds(alice.UserId));
    }
}
