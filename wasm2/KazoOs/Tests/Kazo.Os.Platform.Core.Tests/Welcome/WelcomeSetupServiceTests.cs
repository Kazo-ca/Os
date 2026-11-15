using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Welcome;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Welcome;

/// <summary>ADR 0276 §2, §4, §5, §6: the steps an account still has to see, the state file and the wizard's writes.</summary>
public sealed class WelcomeSetupServiceTests
{
    private static readonly WelcomeStep[] AllSteps = [WelcomeStep.Language, WelcomeStep.Theme, WelcomeStep.Wallpaper, WelcomeStep.TimeZone];

    [Fact]
    public async Task An_account_without_state_sees_the_four_steps()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);

        IReadOnlyList<WelcomeStep> steps = await service.GetPendingStepsAsync(ThemePlatform.Desktop);

        Assert.Equal(AllSteps, steps);
        Assert.Equal(0, settings.Writes);
    }

    [Fact]
    public async Task A_completed_form_factor_shows_nothing_again()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);

        await service.MarkCompletedAsync(ThemePlatform.Desktop);

        Assert.Empty(await new WelcomeSetupService(settings).GetPendingStepsAsync(ThemePlatform.Desktop));
        Assert.Equal("""{"schemaVersion":1,"completed":["desktop"]}""", settings.Files["welcome.json"]);
    }

    [Fact]
    public async Task The_other_form_factor_only_asks_for_its_theme_and_wallpaper()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);
        await service.MarkCompletedAsync(ThemePlatform.Desktop);

        IReadOnlyList<WelcomeStep> steps = await service.GetPendingStepsAsync(ThemePlatform.Mobile);

        Assert.Equal([WelcomeStep.Theme, WelcomeStep.Wallpaper], steps);
        await service.MarkCompletedAsync(ThemePlatform.Mobile);
        Assert.Equal("""{"schemaVersion":1,"completed":["desktop","mobile"]}""", settings.Files["welcome.json"]);
    }

    [Fact]
    public async Task Marking_twice_writes_once()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);

        await service.MarkCompletedAsync(ThemePlatform.Mobile);
        await service.MarkCompletedAsync(ThemePlatform.Mobile);

        Assert.Equal(1, settings.Writes);
    }

    [Fact]
    public async Task Each_account_has_its_own_state()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);
        await service.MarkCompletedAsync(ThemePlatform.Desktop);

        settings.Home = "/home/bob";

        Assert.Equal(AllSteps, await service.GetPendingStepsAsync(ThemePlatform.Desktop));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"completed":"desktop"}""")]
    [InlineData("""{"schemaVersion":1,"completed":["tablet",3]}""")]
    public async Task A_damaged_state_counts_as_nothing_done(string content)
    {
        FakeSettings settings = new();
        settings.Files["welcome.json"] = content;

        Assert.Equal(AllSteps, await new WelcomeSetupService(settings).GetPendingStepsAsync(ThemePlatform.Desktop));
    }

    [Fact]
    public async Task Without_a_session_there_is_no_step_and_no_write()
    {
        FakeSettings settings = new() { Home = null };
        WelcomeSetupService service = new(settings);

        Assert.Empty(await service.GetPendingStepsAsync(ThemePlatform.Desktop));
        await service.MarkCompletedAsync(ThemePlatform.Desktop);
        Assert.Equal(0, settings.Writes);
    }

    [Fact]
    public async Task An_unreadable_state_never_shows_the_wizard()
    {
        FakeSettings settings = new() { FailStateRead = true };

        Assert.Empty(await new WelcomeSetupService(settings).GetPendingStepsAsync(ThemePlatform.Desktop));
    }

    [Fact]
    public async Task A_state_that_cannot_be_written_does_not_throw()
    {
        FakeSettings settings = new() { FailWrites = true };

        await new WelcomeSetupService(settings).MarkCompletedAsync(ThemePlatform.Desktop);

        Assert.Empty(settings.Files);
    }

    [Fact]
    public async Task Locked_settings_leave_their_steps_out()
    {
        FakeSettings settings = new();
        settings.Locked["appearance"] = [AppearanceSettingsDocuments.DesktopThemeKey];
        settings.Locked["datetime"] = [TimeZoneSettingsDocuments.TimeZoneKey];

        IReadOnlyList<WelcomeStep> steps = await new WelcomeSetupService(settings).GetPendingStepsAsync(ThemePlatform.Desktop);

        Assert.Equal([WelcomeStep.Language, WelcomeStep.Wallpaper], steps);
    }

    [Fact]
    public async Task A_lock_on_the_other_form_factor_changes_nothing()
    {
        FakeSettings settings = new();
        settings.Locked["appearance"] = [AppearanceSettingsDocuments.DesktopThemeKey, AppearanceSettingsDocuments.WallpaperKey];

        Assert.Equal(AllSteps, await new WelcomeSetupService(settings).GetPendingStepsAsync(ThemePlatform.Mobile));
    }

    [Fact]
    public async Task When_every_remaining_step_is_locked_the_form_factor_is_marked_done()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);
        await service.MarkCompletedAsync(ThemePlatform.Desktop);
        settings.Locked["appearance"] = [AppearanceSettingsDocuments.MobileThemeKey, AppearanceSettingsDocuments.MobileWallpaperKey];

        Assert.Empty(await service.GetPendingStepsAsync(ThemePlatform.Mobile));

        Assert.Equal("""{"schemaVersion":1,"completed":["desktop","mobile"]}""", settings.Files["welcome.json"]);
    }

    [Fact]
    public async Task Choosing_a_language_writes_LANG_keeps_the_other_variables_and_reloads_the_session()
    {
        FakeSettings settings = new();
        settings.Files[".config/locale.conf"] = "LANG=en_GB.UTF-8\nLC_TIME=fr_CA.UTF-8\n";
        List<string> reloads = [];
        WelcomeSetupService service = new(settings, (principal, _) =>
        {
            reloads.Add(principal.LoginName.Value);
            return Task.CompletedTask;
        });

        await service.SetLanguageAsync("fr_CA.UTF-8", Principal("alice"));

        Assert.Equal(
            $"# {WelcomeSetupService.LocaleFileHeader}\nLANG=fr_CA.UTF-8\nLC_TIME=fr_CA.UTF-8\n",
            settings.Files[".config/locale.conf"]);
        Assert.Equal("fr_CA.UTF-8", await service.GetChosenLocaleAsync());
        Assert.Equal(["alice"], reloads);
    }

    [Fact]
    public async Task Following_the_system_language_removes_LANG()
    {
        FakeSettings settings = new();
        settings.Files[".config/locale.conf"] = "LANG=fr_CA.UTF-8\n";
        int reloads = 0;
        WelcomeSetupService service = new(settings, (_, _) =>
        {
            reloads++;
            return Task.CompletedTask;
        });

        await service.SetLanguageAsync(null, Principal("alice"));

        Assert.Null(await service.GetChosenLocaleAsync());
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task Following_the_system_language_when_already_following_writes_nothing()
    {
        FakeSettings settings = new();
        int reloads = 0;
        WelcomeSetupService service = new(settings, (_, _) =>
        {
            reloads++;
            return Task.CompletedTask;
        });

        await service.SetLanguageAsync(null, Principal("alice"));

        Assert.Equal((0, 0), (settings.Writes, reloads));
    }

    [Fact]
    public async Task An_unknown_language_is_refused()
    {
        FakeSettings settings = new();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await new WelcomeSetupService(settings).SetLanguageAsync("xx_YY.UTF-8", Principal("alice")));

        Assert.Equal(0, settings.Writes);
    }

    [Fact]
    public async Task The_wallpaper_goes_to_the_key_of_the_form_factor()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);

        await service.SetWallpaperAsync(ThemePlatform.Desktop, "builtin:aurora");
        await service.SetWallpaperAsync(ThemePlatform.Mobile, string.Empty);

        Assert.Equal(
            [("appearance", "wallpaper", "builtin:aurora"), ("appearance", "mobileWallpaper", string.Empty)],
            settings.Sets);
    }

    [Fact]
    public async Task A_value_that_is_no_wallpaper_is_refused()
    {
        FakeSettings settings = new();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await new WelcomeSetupService(settings).SetWallpaperAsync(ThemePlatform.Desktop, "builtin:"));

        Assert.Empty(settings.Sets);
    }

    [Fact]
    public async Task The_time_zone_is_written_or_refused()
    {
        FakeSettings settings = new();
        WelcomeSetupService service = new(settings);

        await service.SetTimeZoneAsync("auto");
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.SetTimeZoneAsync("Mars/Olympus_Mons"));

        Assert.Equal([("datetime", "timeZone", "auto")], settings.Sets);
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

    /// <summary>User files kept per home, layered documents reduced to their locked keys and their writes.</summary>
    private sealed class FakeSettings : IOsSettingsService
    {
        private readonly Dictionary<string, Dictionary<string, string>> _homes = new(StringComparer.Ordinal);

        public string? Home { get; set; } = "/home/alice";

        public bool FailStateRead { get; init; }

        public bool FailWrites { get; init; }

        public int Writes { get; private set; }

        public Dictionary<string, string[]> Locked { get; } = new(StringComparer.Ordinal);

        public List<(string Document, string Key, string Value)> Sets { get; } = [];

        public Dictionary<string, string> Files => Home is null ? [] : FilesOf(Home);

        public event EventHandler<OsSettingsChangedEventArgs>? DocumentChanged { add { } remove { } }

        public string? CurrentHomeDirectory => Home;

        public ValueTask<string?> ReadFileAsync(OsSettingsFile file, CancellationToken cancellationToken = default)
        {
            if (FailStateRead && file.Location == OsSettingsLocation.UserState)
            {
                throw new IOException("The storage is unavailable.");
            }

            return ValueTask.FromResult(Home is null ? null : FilesOf(Home).GetValueOrDefault(file.RelativePath));
        }

        public ValueTask WriteFileAsync(OsSettingsFile file, string content, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new IOException("The storage is full.");
            }

            FilesOf(Home ?? throw new InvalidOperationException("No session."))[file.RelativePath] = content;
            Writes++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<LayeredConfigSnapshot> ReadAsync(OsSettingsDocument document, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LayeredConfigSnapshot(
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, SettingLayer>(StringComparer.Ordinal),
                new HashSet<string>(Locked.GetValueOrDefault(document.DocumentId) ?? [], StringComparer.Ordinal),
                []));

        public ValueTask SetAsync(OsSettingsDocument document, string qualifiedKey, string value, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SettingLayer.User, target);
            Sets.Add((document.DocumentId, qualifiedKey, value));
            return ValueTask.CompletedTask;
        }

        public ValueTask SetManyAsync(OsSettingsDocument document, IReadOnlyList<KeyValuePair<string, string>> values, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ResetAsync(OsSettingsDocument document, string qualifiedKey, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string> ReadValidatedFileAsync(OsSettingsFile file, ISettingsDocumentValidator validator, string defaultContent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WatchAsync(OsSettingsDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private Dictionary<string, string> FilesOf(string home)
        {
            if (!_homes.TryGetValue(home, out Dictionary<string, string>? files))
            {
                files = new(StringComparer.Ordinal);
                _homes[home] = files;
            }

            return files;
        }
    }
}
