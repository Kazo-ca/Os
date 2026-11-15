using System.Text;
using System.Text.Json;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Theming.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Welcome;

/// <summary>One step of the welcome wizard (ADR 0276 §2), in display order.</summary>
public enum WelcomeStep
{
    /// <summary>The language of the session: <c>LANG</c> of <c>~/.config/locale.conf</c>.</summary>
    Language = 0,

    /// <summary>The theme of the device's form factor (<c>appearance.config</c>).</summary>
    Theme = 1,

    /// <summary>The wallpaper of the device's form factor (<c>appearance.config</c>).</summary>
    Wallpaper = 2,

    /// <summary>The time zone (<c>datetime.config</c>).</summary>
    TimeZone = 3
}

/// <summary>
/// The welcome wizard's memory and writes (ADR 0276): which steps the session user still has to
/// see on a form factor, the form factors already done, and the language choice that applies at
/// once.
/// </summary>
/// <remarks>
/// <para>
/// The state is one small file per account, <c>~/.local/state/kazoos/welcome.json</c>
/// (<c>{"schemaVersion":1,"completed":["desktop"]}</c>): device-local, never synchronized
/// (ADR 0044 §10). A missing or malformed file means nothing is done, so an account created before
/// the wizard existed sees it once.
/// </para>
/// <para>
/// Nothing here may keep a user away from the desktop: a state that cannot be read plans no step,
/// and a state that cannot be written is only logged (ADR 0276 §6).
/// </para>
/// </remarks>
public sealed class WelcomeSetupService
{
    /// <summary>The account's state file: <c>~/.local/state/kazoos/welcome.json</c>.</summary>
    public static readonly OsSettingsFile StateFile = new(OsSettingsLocation.UserState, "welcome.json");

    /// <summary>The user's locale file, <c>~/.config/locale.conf</c>, read by the login environment (ADR 0104).</summary>
    public static readonly OsSettingsFile UserLocaleFile = new(OsSettingsLocation.UserHome, LoginEnvironment.UserLocaleFileRelativePath);

    /// <summary>The comment written at the top of <c>~/.config/locale.conf</c> (file content, never translated).</summary>
    public const string LocaleFileHeader = "Written by the welcome wizard. Change it in Settings > Language.";

    private const int SchemaVersion = 1;
    private const string DesktopId = "desktop";
    private const string MobileId = "mobile";

    private readonly IOsSettingsService _settings;
    private readonly Func<AuthenticatedPrincipal, CancellationToken, Task>? _reloadLanguage;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="settings">Reads and writes the session user's files, with the user's rights.</param>
    /// <param name="reloadLanguage">
    /// Makes the session take the language just written (the host reloads the login environment and
    /// the shell's locale); <see langword="null"/> leaves it to the next sign-in.
    /// </param>
    /// <param name="logger">Receives the state problems; optional.</param>
    public WelcomeSetupService(
        IOsSettingsService settings,
        Func<AuthenticatedPrincipal, CancellationToken, Task>? reloadLanguage = null,
        ILogger<WelcomeSetupService>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reloadLanguage = reloadLanguage;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the key of <c>appearance.config</c> holding the theme of a form factor.</summary>
    /// <param name="platform">The form factor.</param>
    public static string ThemeKey(ThemePlatform platform) => platform == ThemePlatform.Mobile
        ? AppearanceSettingsDocuments.MobileThemeKey
        : AppearanceSettingsDocuments.DesktopThemeKey;

    /// <summary>Gets the key of <c>appearance.config</c> holding the wallpaper of a form factor.</summary>
    /// <param name="platform">The form factor.</param>
    public static string WallpaperKey(ThemePlatform platform) => platform == ThemePlatform.Mobile
        ? AppearanceSettingsDocuments.MobileWallpaperKey
        : AppearanceSettingsDocuments.WallpaperKey;

    /// <summary>
    /// Gets the steps the session user still has to see on <paramref name="platform"/>, in display
    /// order (ADR 0276 §4 and §5): the four steps when no form factor is done, the theme and the
    /// wallpaper when only the other one is, none when this one is. Steps whose setting the
    /// administrator locked are left out; when none is left the form factor is marked done.
    /// </summary>
    /// <param name="platform">The form factor of the device in use.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The steps to show; empty when the desktop opens directly.</returns>
    public async ValueTask<IReadOnlyList<WelcomeStep>> GetPendingStepsAsync(
        ThemePlatform platform,
        CancellationToken cancellationToken = default)
    {
        if (_settings.CurrentHomeDirectory is null)
        {
            return [];
        }

        HashSet<string> completed;
        try
        {
            completed = ParseState(await _settings.ReadFileAsync(StateFile, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // ADR 0276 §6: an unreadable state never stands between the user and the desktop.
            _logger.LogWarning("welcome: the state could not be read, the wizard is not shown: {Error}", exception.Message);
            return [];
        }

        if (completed.Contains(Id(platform)))
        {
            return [];
        }

        List<WelcomeStep> steps = completed.Count == 0
            ? [WelcomeStep.Language, WelcomeStep.Theme, WelcomeStep.Wallpaper, WelcomeStep.TimeZone]
            : [WelcomeStep.Theme, WelcomeStep.Wallpaper];

        IReadOnlySet<string>? appearanceLocks = await ReadLockedKeysAsync(AppearanceSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
        if (appearanceLocks is null || appearanceLocks.Contains(ThemeKey(platform)))
        {
            steps.Remove(WelcomeStep.Theme);
        }

        if (appearanceLocks is null || appearanceLocks.Contains(WallpaperKey(platform)))
        {
            steps.Remove(WelcomeStep.Wallpaper);
        }

        if (steps.Contains(WelcomeStep.TimeZone))
        {
            IReadOnlySet<string>? timeLocks = await ReadLockedKeysAsync(TimeZoneSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
            if (timeLocks is null || timeLocks.Contains(TimeZoneSettingsDocuments.TimeZoneKey))
            {
                steps.Remove(WelcomeStep.TimeZone);
            }
        }

        if (steps.Count == 0)
        {
            await MarkCompletedAsync(platform, cancellationToken).ConfigureAwait(false);
        }

        return steps;
    }

    /// <summary>
    /// Records that the wizard was finished or skipped on <paramref name="platform"/>. A failed
    /// write is logged and the wizard comes back at the next sign-in (ADR 0276 §6).
    /// </summary>
    /// <param name="platform">The form factor of the device in use.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async ValueTask MarkCompletedAsync(ThemePlatform platform, CancellationToken cancellationToken = default)
    {
        if (_settings.CurrentHomeDirectory is null)
        {
            return;
        }

        try
        {
            HashSet<string> completed = ParseState(await _settings.ReadFileAsync(StateFile, cancellationToken).ConfigureAwait(false));
            if (completed.Add(Id(platform)))
            {
                await _settings.WriteFileAsync(StateFile, SerializeState(completed), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning("welcome: the state could not be written, the wizard will show again: {Error}", exception.Message);
        }
    }

    /// <summary>Gets the <c>LANG</c> of the user's <c>~/.config/locale.conf</c>, or <see langword="null"/> when the user follows the system language.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<string?> GetChosenLocaleAsync(CancellationToken cancellationToken = default)
    {
        string? content = await _settings.ReadFileAsync(UserLocaleFile, cancellationToken).ConfigureAwait(false);
        return KazoLocale.ParseLocaleConf(content).GetValueOrDefault(KazoLocale.LangVariable);
    }

    /// <summary>
    /// Writes the user's language and makes the session take it at once (ADR 0276 §2.1): the
    /// <c>LANG</c> line of <c>~/.config/locale.conf</c>, the other variables of the file kept.
    /// </summary>
    /// <param name="locale">One of <see cref="KazoLocale.AvailableLocales"/>, or <see langword="null"/> to follow the system language.</param>
    /// <param name="principal">The session user.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="locale"/> is not a locale Kazo OS knows.</exception>
    public async ValueTask SetLanguageAsync(string? locale, AuthenticatedPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (locale is not null && !KazoLocale.IsAvailable(locale))
        {
            throw new ArgumentException($"'{locale}' is not an available locale.", nameof(locale));
        }

        string? content = await _settings.ReadFileAsync(UserLocaleFile, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> variables = new(KazoLocale.ParseLocaleConf(content), StringComparer.Ordinal);
        if (locale is null)
        {
            if (!variables.Remove(KazoLocale.LangVariable))
            {
                // Already following the system: nothing to write, nothing to reload.
                return;
            }
        }
        else
        {
            variables[KazoLocale.LangVariable] = locale;
        }

        // With no variable left the file keeps its comment only: it sets nothing, as an absent file.
        await _settings
            .WriteFileAsync(UserLocaleFile, KazoLocale.FormatLocaleConf(variables, LocaleFileHeader), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (_reloadLanguage is not null)
        {
            await _reloadLanguage(principal, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the wallpaper of a form factor to the user's <c>appearance.config</c> (ADR 0276 §2.3).</summary>
    /// <param name="platform">The form factor of the device in use.</param>
    /// <param name="value">An empty string (the theme's own background) or <c>builtin:&lt;id&gt;</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a wallpaper value.</exception>
    public ValueTask SetWallpaperAsync(ThemePlatform platform, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && !WallpaperCatalog.IsValidValue(value))
        {
            throw new ArgumentException($"'{value}' is not a wallpaper.", nameof(value));
        }

        return _settings.SetAsync(AppearanceSettingsDocuments.Document, WallpaperKey(platform), value, cancellationToken: cancellationToken);
    }

    /// <summary>Writes the time zone to the user's <c>datetime.config</c> (ADR 0276 §2.4).</summary>
    /// <param name="value"><see cref="KazoTimeZone.Automatic"/> or an identifier of <see cref="KazoTimeZone.Available"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> names no known time zone.</exception>
    public ValueTask SetTimeZoneAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value != KazoTimeZone.Automatic && !KazoTimeZone.TryFind(value, out _))
        {
            throw new ArgumentException($"'{value}' is not a known time zone.", nameof(value));
        }

        return _settings.SetAsync(TimeZoneSettingsDocuments.Document, TimeZoneSettingsDocuments.TimeZoneKey, value, cancellationToken: cancellationToken);
    }

    /// <summary>Reads a state file: the form factors done; empty when the file is missing or malformed.</summary>
    /// <param name="content">The file's text, or <see langword="null"/>.</param>
    internal static HashSet<string> ParseState(string? content)
    {
        HashSet<string> completed = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content))
        {
            return completed;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(content);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("completed", out JsonElement ids)
                && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in ids.EnumerateArray())
                {
                    // Unknown names are dropped: only the two form factors mean something.
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is DesktopId or MobileId)
                    {
                        completed.Add(id.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A damaged file is an absent one: the wizard shows again and rewrites it.
        }

        return completed;
    }

    /// <summary>Writes the state file's text, form factors in a stable order.</summary>
    /// <param name="completed">The form factors done.</param>
    internal static string SerializeState(IEnumerable<string> completed)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("completed");
            foreach (string id in completed.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Id(ThemePlatform platform) => platform == ThemePlatform.Mobile ? MobileId : DesktopId;

    /// <summary>Reads the locked keys of a document; <see langword="null"/> when it cannot be read (its steps are then left out).</summary>
    private async ValueTask<IReadOnlySet<string>?> ReadLockedKeysAsync(OsSettingsDocument document, CancellationToken cancellationToken)
    {
        try
        {
            LayeredConfigSnapshot snapshot = await _settings.ReadAsync(document, cancellationToken).ConfigureAwait(false);
            return snapshot.LockedKeys;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning("welcome: {Document}.config could not be read, its steps are left out: {Error}", document.DocumentId, exception.Message);
            return null;
        }
    }
}
