using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Welcome;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The welcome wizard (ADR 0276): language, theme, wallpaper and time zone, offered once per
/// account and form factor between the sign-in progress and the desktop.
/// </summary>
/// <remarks>
/// <para>
/// The session is already open, so every choice is written at once to the user's own files through
/// <see cref="WelcomeSetupService"/> and <see cref="ThemePreferenceService"/>, with the user's
/// rights: the theme repaints the whole host on click, the language changes the texts of this very
/// screen. « Skip » and « Finish » both record the form factor as done and keep what was chosen.
/// </para>
/// <para>
/// A write that fails shows its error under the step and never blocks the way to the desktop
/// (ADR 0276 §6).
/// </para>
/// </remarks>
public sealed partial class WelcomeWizard : ComponentBase, IDisposable
{
    /// <summary>The most time zones listed at once; the search box narrows the list.</summary>
    internal const int MaxShownZones = 40;

    private ElementReference _title;
    private int _index;
    private bool _focusTitle;
    private bool _isBusy;
    private string? _pendingThemeId;
    private string? _error;
    private string? _chosenLocale;
    private string _wallpaper = string.Empty;
    private string _zoneFilter = string.Empty;

    /// <summary>Gets or sets the steps to show, in order (never empty): <see cref="WelcomeSetupService.GetPendingStepsAsync"/>.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<WelcomeStep> Steps { get; set; } = [];

    /// <summary>Gets or sets the form factor of the device in use: its themes and its wallpaper are offered.</summary>
    [Parameter]
    public ThemePlatform Platform { get; set; } = ThemePlatform.Desktop;

    /// <summary>Gets or sets the callback run once the wizard is finished or skipped: the host shows the desktop.</summary>
    [Parameter]
    public EventCallback OnFinished { get; set; }

    [Inject]
    private WelcomeSetupService Welcome { get; set; } = default!;

    [Inject]
    private ThemePreferenceService ThemePreference { get; set; } = default!;

    [Inject]
    private UserThemeCatalogService UserThemes { get; set; } = default!;

    [Inject]
    private SessionTimeZoneService TimeZones { get; set; } = default!;

    [Inject]
    private SessionLocaleService SessionLocale { get; set; } = default!;

    [Inject]
    private ISessionService Sessions { get; set; } = default!;

    [Inject]
    private IOsSettingsService Settings { get; set; } = default!;

    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    /// <summary>Gets the languages offered: the locales of the OS, without the bare <c>C</c> locale.</summary>
    internal static IReadOnlyList<string> Locales { get; } =
        [.. KazoLocale.AvailableLocales.Where(static locale => !locale.StartsWith("C.", StringComparison.Ordinal))];

    private WelcomeStep CurrentStep => Steps[Math.Clamp(_index, 0, Steps.Count - 1)];

    private string StepTitle => L[$"Welcome.{CurrentStep}.Title"];

    private string StepIntro => L[$"Welcome.{CurrentStep}.Intro"];

    private IReadOnlyList<ThemeDefinition> BuiltInThemes =>
        Platform == ThemePlatform.Mobile ? ThemeCatalog.MobileThemes : ThemeCatalog.DesktopThemes;

    private IEnumerable<UserTheme> UserThemesForPlatform =>
        UserThemes.Current.Themes.Where(theme => theme.Supports(Platform));

    /// <summary>The theme the shell renders: an id naming no theme of this form factor shows the default one selected.</summary>
    private string SelectedThemeId
    {
        get
        {
            if (_pendingThemeId is not null)
            {
                return _pendingThemeId;
            }

            string saved = Platform == ThemePlatform.Mobile
                ? ThemePreference.Current.MobileThemeId
                : ThemePreference.Current.DesktopThemeId;
            ResolvedTheme resolved = UserThemes.Resolve(saved, Platform);
            return resolved.UserTheme?.Id ?? resolved.BaseThemeId;
        }
    }

    private bool IsAutomaticZone => !KazoTimeZone.TryFind(TimeZones.Setting, out _);

    private static DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();

    private string LocalTime => TimeZones.ToLocal(UtcNow).ToString("t", SessionLocale.FormatCulture);

    private IEnumerable<TimeZoneInfo> MatchingZones => KazoTimeZone.Available.Where(MatchesFilter);

    private IEnumerable<TimeZoneInfo> ShownZones => MatchingZones.Take(MaxShownZones);

    private int HiddenZoneCount => Math.Max(0, MatchingZones.Count() - MaxShownZones);

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        SessionLocale.Changed += OnShellStateChanged;
        ThemePreference.Changed += OnShellStateChanged;
        TimeZones.Changed += OnShellStateChanged;
        UserThemes.Changed += OnShellStateChanged;

        // The shell's services follow the session by themselves, a moment after the sign-in: the
        // wizard reads the user's files itself so the first render already shows their choices.
        await RunAsync(async () =>
        {
            await ThemePreference.RefreshAsync();
            await TimeZones.RefreshAsync();
            await UserThemes.RefreshAsync();
            _chosenLocale = await Welcome.GetChosenLocaleAsync();
            LayeredConfigSnapshot appearance = await Settings.ReadAsync(AppearanceSettingsDocuments.Document);
            _wallpaper = appearance.Values.GetValueOrDefault(WelcomeSetupService.WallpaperKey(Platform)) ?? string.Empty;
        });
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender || _focusTitle)
        {
            // A screen reader announces each step: the heading takes the focus when it changes.
            _focusTitle = false;
            try
            {
                await _title.FocusAsync();
            }
            catch (Exception exception) when (exception is InvalidOperationException or Microsoft.JSInterop.JSException)
            {
                // Prerendering or a detached element: the focus stays where it is.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SessionLocale.Changed -= OnShellStateChanged;
        ThemePreference.Changed -= OnShellStateChanged;
        TimeZones.Changed -= OnShellStateChanged;
        UserThemes.Changed -= OnShellStateChanged;
    }

    /// <summary>Writes the language and makes this screen, then the desktop, speak it.</summary>
    /// <param name="locale">A locale of <see cref="Locales"/>, or <see langword="null"/> to follow the system language.</param>
    internal Task ChooseLanguageAsync(string? locale) => RunAsync(async () =>
    {
        AuthenticatedPrincipal principal = Sessions.CurrentPrincipal
            ?? throw new InvalidOperationException("The welcome wizard needs an open session.");
        await Welcome.SetLanguageAsync(locale, principal);
        _chosenLocale = locale;
    });

    /// <summary>Writes the theme of the device's form factor; the host repaints at once.</summary>
    /// <param name="themeId">A built-in or user theme id.</param>
    internal Task ChooseThemeAsync(string themeId) => RunAsync(async () =>
    {
        // The card shows selected at the click; the write and the repaint follow within a second.
        _pendingThemeId = themeId;
        try
        {
            ThemePreferences current = ThemePreference.Current;
            await ThemePreference.SetAsync(Platform == ThemePlatform.Mobile
                ? current with { MobileThemeId = themeId }
                : current with { DesktopThemeId = themeId });
        }
        finally
        {
            _pendingThemeId = null;
        }
    });

    /// <summary>Writes the wallpaper of the device's form factor.</summary>
    /// <param name="value">An empty string (the theme's background) or a <c>builtin:</c> value.</param>
    internal Task ChooseWallpaperAsync(string value) => RunAsync(async () =>
    {
        await Welcome.SetWallpaperAsync(Platform, value);
        _wallpaper = value;
    });

    /// <summary>Writes the time zone; the clock of this step follows.</summary>
    /// <param name="value"><see cref="KazoTimeZone.Automatic"/> or a zone identifier.</param>
    internal Task ChooseTimeZoneAsync(string value) => RunAsync(async () =>
    {
        await Welcome.SetTimeZoneAsync(value);
        await TimeZones.RefreshAsync();
    });

    /// <summary>« Skip » and « Finish »: records the form factor as done and hands over to the desktop.</summary>
    internal async Task FinishAsync()
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        try
        {
            // Never throws on a failed write (ADR 0276 §6): the desktop opens, the wizard comes back.
            await Welcome.MarkCompletedAsync(Platform);
        }
        finally
        {
            _isBusy = false;
        }

        await OnFinished.InvokeAsync();
    }

    private void GoNext() => MoveTo(_index + 1);

    private void GoBack() => MoveTo(_index - 1);

    private void MoveTo(int index)
    {
        _index = Math.Clamp(index, 0, Steps.Count - 1);
        _error = null;
        _focusTitle = true;
    }

    /// <summary>Runs one write, one at a time, and shows its failure under the step.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        _error = null;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _error = L["Welcome.Error", exception.Message];
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void OnShellStateChanged() => _ = InvokeAsync(StateHasChanged);

    private bool IsChosenLocale(string locale) =>
        _chosenLocale is not null && string.Equals(_chosenLocale, locale, StringComparison.OrdinalIgnoreCase);

    private bool IsChosenZone(string id) => string.Equals(TimeZones.Setting, id, StringComparison.Ordinal);

    private bool MatchesFilter(TimeZoneInfo zone)
    {
        string filter = _zoneFilter.Trim();
        return filter.Length == 0
            || KazoTimeZone.GetId(zone).Contains(filter.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase)
            || zone.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string ZoneLabel(TimeZoneInfo zone) =>
        $"{KazoTimeZone.GetId(zone)} ({KazoTimeZone.FormatOffset(zone, UtcNow)})";

    /// <summary>The name of a locale in its own language: "Français (Canada)", "English (United Kingdom)".</summary>
    internal static string LocaleName(string locale)
    {
        CultureInfo? culture = KazoLocale.ToCulture(locale);
        if (culture is null || culture.Name.Length == 0)
        {
            return locale;
        }

        string name = culture.NativeName;
        return name.Length == 0 ? locale : char.ToUpper(name[0], culture) + name[1..];
    }

    /// <summary>The BCP 47 tag of a locale, for the <c>lang</c> attribute of its own name.</summary>
    private static string LocaleTag(string locale) => KazoLocale.ToCulture(locale)?.Name ?? "en";

    /// <summary>The display name of a built-in wallpaper; its id when a new one has no text yet.</summary>
    private string WallpaperName(BuiltInWallpaper wallpaper)
    {
        LocalizedString name = L[$"Welcome.Wallpaper.{wallpaper.Id}"];
        return name.ResourceNotFound ? wallpaper.Id : name.Value;
    }

    private static string OptionClass(bool selected) => selected ? "welcome-option is-selected" : "welcome-option";

    private static string CardClass(bool selected) => selected ? "welcome-card is-selected" : "welcome-card";

    private string ProgressClass(int position) => position == _index
        ? "is-current"
        : position < _index ? "is-done" : "is-pending";
}
