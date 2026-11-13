using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Defines the per-user start-menu document <c>~/.config/kazoos/start-menu.json</c> (ADR 0044
/// section 8): strict JSON, user layer only, one file per user in that user's own home.
/// </summary>
/// <remarks>
/// The file keeps the historical shape <c>{"schemaVersion":1,"profiles":{"&lt;userId&gt;":{"pinnedAppIds":[...]}}}</c>
/// with a single profile, so the validator and codec are shared with the migration from the old
/// aggregated <c>/etc/kazoos/start-menu.json</c> store record.
/// </remarks>
public static class StartMenuSettingsDocuments
{
    /// <summary>The maximum number of ordered quick-launch pins stored for one local user.</summary>
    public const int MaximumPinnedAppCount = 64;

    /// <summary>Gets the historical settings-store identity, read only by the migration.</summary>
    public static SettingsDocumentKey Key { get; } = SettingsDocumentKey.ForOsAdmin("start-menu");

    /// <summary>Gets the per-user file.</summary>
    public static OsSettingsFile File { get; } = new(OsSettingsLocation.UserConfig, "start-menu.json");

    /// <summary>
    /// Property of a profile listing the default pins already added once (ADR 0212 §6), so an app the
    /// user unpinned is not pinned again.
    /// </summary>
    public const string DefaultPinsAppliedProperty = "defaultPinsApplied";

    /// <summary>
    /// Gets the apps pinned by default (ADR 0212 §6): to every new profile, and added once to the
    /// existing profiles. The user may unpin them. Empty since ADR 0268: the Store moved to the
    /// Start menu's Quick access panel.
    /// </summary>
    public static IReadOnlyList<string> DefaultPinnedAppIds { get; } = [];

    /// <summary>
    /// Gets the apps that used to be pinned by default and no longer are (ADR 0268). A profile whose
    /// <c>defaultPinsApplied</c> still lists one loses that pin once and forgets it, so a pin the
    /// user adds back afterwards stays.
    /// </summary>
    public static IReadOnlyList<string> RetiredDefaultPinnedAppIds { get; } = ["org.kazoos.store"];

    /// <summary>Gets the clean-profile document containing no pins.</summary>
    public const string EmptyDocumentContent = "{\"schemaVersion\":1,\"profiles\":{}}";
}
