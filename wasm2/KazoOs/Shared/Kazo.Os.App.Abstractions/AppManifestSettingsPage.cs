namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Options of a <see cref="AppKind.SettingsPage"/> entry point of a feature (ADR 0249 §2), written as
/// the optional <c>settingsPage</c> object of <c>manifest.json</c>. The page's title is the entry
/// point's name and its icon the first of its presentation icons.
/// </summary>
public sealed record SettingsPageOptions
{
    /// <summary>The options of a page that declares none: first of its feature, shown on a phone.</summary>
    public static SettingsPageOptions Default { get; } = new();

    /// <summary>
    /// Gets the place of the page among the pages of its feature in Settings ▸ Features: the lowest
    /// first, then by title. Defaults to <c>0</c>.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Gets whether the page shows in Settings on a phone (ADR 0171: full screen, like the pages of
    /// Settings). <see langword="false"/> keeps it on the desktop only. Defaults to <see langword="true"/>.
    /// </summary>
    public bool MobileSupported { get; init; } = true;

    /// <summary>Gets the options a manifest declares, or <see cref="Default"/> when it declares none.</summary>
    /// <param name="manifest">A settings page entry point.</param>
    /// <returns>Its options.</returns>
    public static SettingsPageOptions Of(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.SettingsPage ?? Default;
    }
}
