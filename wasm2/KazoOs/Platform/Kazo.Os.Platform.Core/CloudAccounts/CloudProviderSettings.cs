using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// The public app ids of the cloud providers (ADR 0267 section 5), from the host's
/// <c>appsettings.json</c> (<c>KazoOs:CloudDrives</c>). They are public by design: OAuth with PKCE
/// uses no secret. An empty id means the provider (or that scope) is not offered.
/// </summary>
/// <param name="DropboxFullAccessClientId">App key of the "Full Dropbox" app.</param>
/// <param name="DropboxAppFolderClientId">App key of the "App folder" app.</param>
/// <param name="OneDriveClientId">Application (client) id of the Microsoft Entra registration.</param>
/// <param name="OneDriveTenant">Microsoft tenant; empty means <c>common</c>.</param>
/// <param name="DropboxAppFolderPath">Where Dropbox puts the "App folder" app's folder, as the user sees it (<c>Apps/Kazo OS Folder</c>); shown before the start folder.</param>
/// <param name="OneDriveAppFolderPath">Where OneDrive puts the folder reserved for the app, as the user sees it (<c>Apps/Kazo OS</c>).</param>
public sealed record CloudProviderSettings(
    string? DropboxFullAccessClientId = null,
    string? DropboxAppFolderClientId = null,
    string? OneDriveClientId = null,
    string? OneDriveTenant = null,
    string? DropboxAppFolderPath = null,
    string? OneDriveAppFolderPath = null)
{
    /// <summary>Section of <c>appsettings.json</c>: <c>KazoOs:CloudDrives</c>.</summary>
    public const string SectionName = "KazoOs:CloudDrives";

    /// <summary>No app id at all: no provider is offered.</summary>
    public static CloudProviderSettings None { get; } = new();

    /// <summary>
    /// Gets the drive types switched off on this host (ADR 0282 section 5): <c>dropbox</c>,
    /// <c>onedrive</c>, <c>s3</c>. They come from <c>KazoOs:CloudDrives:Disabled</c> and from the
    /// connectors the build left out; <see cref="OverriddenBy"/> adds the <c>disabled</c> key of
    /// <c>/etc/kazoos/cloud-drives.config</c>. A disabled type is never offered, even with an app id.
    /// </summary>
    public IReadOnlyCollection<string> Disabled { get; init; } = [];

    /// <summary>Gets whether <paramref name="type"/> is switched off on this host.</summary>
    /// <param name="type">A drive type or provider id, e.g. <c>dropbox</c>.</param>
    public bool IsDisabled(string type) => Disabled.Contains(type, StringComparer.Ordinal);

    /// <summary>
    /// Reads a list of drive types as an administrator writes it: separated by commas, semicolons or
    /// spaces, case ignored (<c>"Dropbox, s3"</c>). Empty entries are ignored.
    /// </summary>
    /// <param name="value">The text of <c>KazoOs:CloudDrives:Disabled</c> or of the <c>disabled</c> key.</param>
    /// <returns>The types in lower case, each once.</returns>
    public static IReadOnlyList<string> ParseTypes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static type => type.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Applies the machine's <c>/etc/kazoos/cloud-drives.config</c> over these settings: a key
    /// written there with a value wins, an absent or empty key keeps the host's value. This lets an
    /// administrator who hosts Kazo OS at another address use their own registrations.
    /// </summary>
    /// <param name="snapshot">Snapshot of <see cref="CloudDrivesSettingsDocuments.Document"/>.</param>
    public CloudProviderSettings OverriddenBy(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CloudProviderSettings(
            Pick(snapshot, CloudDrivesSettingsDocuments.DropboxFullAccessQualifiedKey, DropboxFullAccessClientId),
            Pick(snapshot, CloudDrivesSettingsDocuments.DropboxAppFolderQualifiedKey, DropboxAppFolderClientId),
            Pick(snapshot, CloudDrivesSettingsDocuments.OneDriveClientQualifiedKey, OneDriveClientId),
            Pick(snapshot, CloudDrivesSettingsDocuments.OneDriveTenantQualifiedKey, OneDriveTenant),
            Pick(snapshot, CloudDrivesSettingsDocuments.DropboxAppFolderPathQualifiedKey, DropboxAppFolderPath),
            Pick(snapshot, CloudDrivesSettingsDocuments.OneDriveAppFolderPathQualifiedKey, OneDriveAppFolderPath))
        {
            // Both lists switch a type off: the machine's file can add to the host's list, never
            // turn back on what the host or the build left out.
            Disabled = snapshot.Values.TryGetValue(CloudDrivesSettingsDocuments.DisabledKey, out string? disabled)
                ? [.. Disabled.Union(ParseTypes(disabled), StringComparer.Ordinal)]
                : Disabled
        };
    }

    /// <summary>Builds the providers these settings configure, Dropbox first; a provider without any app id, or disabled, is left out.</summary>
    public IReadOnlyList<CloudOAuthProvider> BuildProviders() =>
    [
        .. new[]
        {
            CloudOAuthProvider.Dropbox(DropboxFullAccessClientId, DropboxAppFolderClientId) is { } dropbox
                ? dropbox with { AppFolderPath = Clean(DropboxAppFolderPath) }
                : null,
            CloudOAuthProvider.OneDrive(OneDriveClientId, OneDriveTenant) is { } oneDrive
                ? oneDrive with { AppFolderPath = Clean(OneDriveAppFolderPath) }
                : null
        }.OfType<CloudOAuthProvider>().Where(provider => !IsDisabled(provider.Id))
    ];

    /// <summary>A folder path as the dialog shows it: trimmed, no slash at either end; empty is none.</summary>
    private static string? Clean(string? path) =>
        path?.Trim().Trim('/', '\\').Trim() is { Length: > 0 } cleaned ? cleaned : null;

    private static string? Pick(LayeredConfigSnapshot snapshot, string qualifiedKey, string? fallback) =>
        snapshot.Values.TryGetValue(qualifiedKey, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
}

/// <summary>
/// Defines <c>/etc/kazoos/cloud-drives.config</c> (ADR 0267 section 5), system layer only: the app
/// ids an administrator uses instead of the host's, for a Kazo OS served at another address (the
/// redirect URI registered with a provider names one address).
/// </summary>
/// <example>
/// <code>
/// [dropbox]
/// full-access-client-id = abcd1234
/// app-folder-client-id = efgh5678
///
/// [onedrive]
/// client-id = 00000000-0000-0000-0000-000000000000
/// tenant = common
/// </code>
/// A <c>disabled</c> key at the top switches shipped drive types off (ADR 0282 section 5):
/// <code>
/// disabled = dropbox, s3
/// </code>
/// </example>
public static class CloudDrivesSettingsDocuments
{
    /// <summary>Document id, giving <c>cloud-drives.config</c>.</summary>
    public const string DocumentId = "cloud-drives";

    /// <summary>Section of the Dropbox app keys.</summary>
    public const string DropboxGroup = "dropbox";

    /// <summary>Section of the Microsoft registration.</summary>
    public const string OneDriveGroup = "onedrive";

    /// <summary>Gets <c>dropbox.full-access-client-id</c>.</summary>
    public static string DropboxFullAccessQualifiedKey => $"{DropboxGroup}.full-access-client-id";

    /// <summary>Gets <c>dropbox.app-folder-client-id</c>.</summary>
    public static string DropboxAppFolderQualifiedKey => $"{DropboxGroup}.app-folder-client-id";

    /// <summary>Gets <c>onedrive.client-id</c>.</summary>
    public static string OneDriveClientQualifiedKey => $"{OneDriveGroup}.client-id";

    /// <summary>Gets <c>onedrive.tenant</c>.</summary>
    public static string OneDriveTenantQualifiedKey => $"{OneDriveGroup}.tenant";

    /// <summary>Gets the key of the Dropbox reserved folder as the user sees it (<c>Apps/Kazo OS Folder</c>).</summary>
    public static string DropboxAppFolderPathQualifiedKey => $"{DropboxGroup}.app-folder-path";

    /// <summary>Gets the key of the OneDrive reserved folder as the user sees it (<c>Apps/Kazo OS</c>).</summary>
    public static string OneDriveAppFolderPathQualifiedKey => $"{OneDriveGroup}.app-folder-path";

    /// <summary>Key of the drive types switched off on this machine (ADR 0282 section 5), outside any section.</summary>
    public const string DisabledKey = "disabled";

    /// <summary>Gets the declared schema: seven public strings, empty by default (empty keeps the host's value).</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration("full-access-client-id", SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: DropboxGroup),
            new SettingFieldDeclaration("app-folder-client-id", SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: DropboxGroup),
            new SettingFieldDeclaration("client-id", SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: OneDriveGroup),
            new SettingFieldDeclaration("tenant", SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: OneDriveGroup),
            new SettingFieldDeclaration("app-folder-path", SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: DropboxGroup),
            new SettingFieldDeclaration("app-folder-path", SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: OneDriveGroup),
            new SettingFieldDeclaration(DisabledKey, SettingValueType.String, string.Empty, SettingSensitivity.Public)
        ]);

    /// <summary>Gets the system-only layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema, SystemOnly: true);
}

/// <summary>The providers this OS can connect, read when asked.</summary>
public interface ICloudProviderCatalog
{
    /// <summary>Gets the configured providers.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The host's app ids (<see cref="CloudProviderSettings"/>) overridden by
/// <c>/etc/kazoos/cloud-drives.config</c> when that file sets them.
/// </summary>
/// <param name="hostSettings">The host's <c>appsettings.json</c> values.</param>
/// <param name="settings">Reads the system document; <see langword="null"/> uses the host's values alone.</param>
public sealed class CloudProviderCatalog(CloudProviderSettings hostSettings, IOsSettingsService? settings = null) : ICloudProviderCatalog
{
    private readonly CloudProviderSettings _hostSettings = hostSettings ?? throw new ArgumentNullException(nameof(hostSettings));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        (await GetEffectiveSettingsAsync(cancellationToken).ConfigureAwait(false)).BuildProviders();

    /// <summary>
    /// Gets whether a shipped drive type may be offered now (ADR 0282 section 5): not left out by the
    /// build, not in <c>KazoOs:CloudDrives:Disabled</c>, not in the <c>disabled</c> key of
    /// <c>/etc/kazoos/cloud-drives.config</c>. The file is read on each call, so a change applies
    /// without restarting the OS.
    /// </summary>
    /// <param name="type">The drive type, e.g. <c>s3</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<bool> IsTypeEnabledAsync(string type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        return !(await GetEffectiveSettingsAsync(cancellationToken).ConfigureAwait(false)).IsDisabled(type);
    }

    private async ValueTask<CloudProviderSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        CloudProviderSettings effective = _hostSettings;
        if (settings is not null)
        {
            LayeredConfigSnapshot snapshot = await settings
                .ReadAsync(CloudDrivesSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            effective = effective.OverriddenBy(snapshot);
        }

        return effective;
    }
}
