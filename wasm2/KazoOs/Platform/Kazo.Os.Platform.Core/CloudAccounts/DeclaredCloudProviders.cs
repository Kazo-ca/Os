using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// The cloud providers that installed features declare (ADR 0282 §3): an OAuth drive type of a
/// feature's <c>driveProviders</c> becomes a <see cref="CloudOAuthProvider"/>, so the OS signs the
/// user in for it exactly as for Dropbox and OneDrive (PKCE, the same callback page, tokens sealed
/// on the device, ADR 0269). The feature never runs the sign-in and never sees a refresh token.
/// </summary>
/// <remarks>
/// <para>
/// An administrator who serves Kazo OS at another address replaces the public app ids of a
/// declared type in <c>/etc/kazoos/cloud-drives.config</c>, one section per type:
/// <code>
/// [pcloud]
/// client-id = my-own-app-id
/// app-folder-client-id = my-folder-app-id
/// </code>
/// <c>client-id</c> is the app id of the access to all of the storage, or of the only access the
/// provider declares; <c>app-folder-client-id</c> is the app id of the reserved folder when the
/// provider declares both. An empty or absent key keeps the manifest's app id.
/// </para>
/// <para>
/// A type named <c>locked</c> cannot have such a section: that name is the settings files' section
/// of locked keys (ADR 0044). Its manifest app ids are always used.
/// </para>
/// </remarks>
public static class DeclaredCloudProviders
{
    /// <summary>Key of a declared type's app id: of the full access, or of its only access.</summary>
    public const string ClientIdKey = "client-id";

    /// <summary>Key of a declared type's app id of the reserved folder, when it declares both accesses.</summary>
    public const string AppFolderClientIdKey = "app-folder-client-id";

    /// <summary>
    /// Gets the OAuth drive types of the enabled features of <paramref name="catalog"/>, one per type
    /// (the first feature in id order when two declare it), the types of the OS left out.
    /// </summary>
    /// <param name="catalog">The app catalog.</param>
    /// <param name="enablement">Which apps are enabled; <see langword="null"/> takes every feature of the catalog as enabled.</param>
    /// <returns>The declarations, ordered by type.</returns>
    public static IReadOnlyList<AppDriveProviderRegistration> GetOAuthRegistrations(AppCatalog catalog, IAppEnablementRegistry? enablement)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. catalog.GetDriveProviders()
            .Where(static registration => registration.Provider.Connection is { Kind: AppDriveConnectionKind.OAuth, OAuth: not null })
            .Where(static registration => !AppDriveProviders.IsReservedType(registration.Provider.Type))
            .Where(registration => enablement?.IsEnabled(registration.Feature.Id) ?? true)
            .GroupBy(static registration => registration.Provider.Type, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static registration => registration.Provider.Type, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Builds the settings document <c>/etc/kazoos/cloud-drives.config</c> with a section per declared
    /// type on top of the shipped providers' keys, so the values of those sections can be read.
    /// </summary>
    /// <param name="baseDocument">The document of the shipped providers (<see cref="CloudDrivesSettingsDocuments.Document"/>).</param>
    /// <param name="types">The declared types.</param>
    /// <returns>The document; <paramref name="baseDocument"/> itself when no type adds a section.</returns>
    public static OsSettingsDocument SettingsDocument(OsSettingsDocument baseDocument, IEnumerable<string> types)
    {
        ArgumentNullException.ThrowIfNull(baseDocument);
        ArgumentNullException.ThrowIfNull(types);
        HashSet<string> taken = [.. baseDocument.Schema.Fields.Values.Select(static field => field.Group ?? string.Empty)];
        SettingFieldDeclaration[] added =
        [
            .. types
                .Where(static type => AppDriveProviders.IsValidType(type)
                    && !string.Equals(type, LayeredConfigResolver.LockedSectionName, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Where(type => !taken.Contains(type))
                .SelectMany(static type => new[]
                {
                    new SettingFieldDeclaration(ClientIdKey, SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: type),
                    new SettingFieldDeclaration(AppFolderClientIdKey, SettingValueType.String, string.Empty, SettingSensitivity.Public, Group: type)
                })
        ];
        if (added.Length == 0)
        {
            return baseDocument;
        }

        SettingsSchema schema = new(baseDocument.Schema.SchemaVersion, [.. baseDocument.Schema.Fields.Values, .. added]);
        return baseDocument with { Schema = schema };
    }

    /// <summary>
    /// Builds the OAuth provider of one declared type: the manifest's addresses, with its app ids
    /// replaced by those of <paramref name="snapshot"/> when the administrator set them.
    /// </summary>
    /// <param name="registration">The feature and its declaration.</param>
    /// <param name="snapshot">Snapshot of <see cref="SettingsDocument"/>, or <see langword="null"/> to use the manifest's app ids.</param>
    /// <returns>The provider, or <see langword="null"/> when the declaration is not OAuth, has no app id, or an address is not absolute <c>https</c>.</returns>
    public static CloudOAuthProvider? Build(AppDriveProviderRegistration registration, LayeredConfigSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        AppDriveProviderManifest declaration = registration.Provider;
        if (declaration.Connection is not { Kind: AppDriveConnectionKind.OAuth, OAuth: { } oauth }
            || HttpsAddress(oauth.AuthorizationEndpoint) is not { } authorization
            || HttpsAddress(oauth.TokenEndpoint) is not { } token)
        {
            return null;
        }

        Uri? revocation = null;
        if (oauth.RevocationEndpoint is { } revocationText && (revocation = HttpsAddress(revocationText)) is null)
        {
            return null;
        }

        CloudIdentityEndpoint? identity = null;
        if (oauth.Identity is { } declaredIdentity)
        {
            if (HttpsAddress(declaredIdentity.Address) is not { } identityAddress)
            {
                return null;
            }

            identity = new CloudIdentityEndpoint(identityAddress, declaredIdentity.UsePost, declaredIdentity.IdPath, [.. declaredIdentity.NamePaths]);
        }

        // client-id names the full access, or the only access; app-folder-client-id the reserved
        // folder when both exist (see the remarks of this class).
        bool both = oauth.FullAccess is not null && oauth.AppFolder is not null;
        string? fullOverride = oauth.FullAccess is null ? null : Override(snapshot, declaration.Type, ClientIdKey);
        string? folderOverride = oauth.AppFolder is null
            ? null
            : Override(snapshot, declaration.Type, both ? AppFolderClientIdKey : ClientIdKey);
        Dictionary<CloudAccessScope, CloudOAuthClient> clients = [];
        AddClient(clients, CloudAccessScope.Full, oauth.FullAccess, fullOverride);
        AddClient(clients, CloudAccessScope.AppFolder, oauth.AppFolder, folderOverride);
        if (clients.Count == 0)
        {
            return null;
        }

        return new CloudOAuthProvider(
            declaration.Type,
            declaration.Name,
            authorization,
            token,
            revocation,
            clients,
            new Dictionary<string, string>(oauth.ExtraAuthorizationParameters ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            identity)
        {
            AppFolderPath = oauth.AppFolderPath?.Trim().Trim('/', '\\').Trim() is { Length: > 0 } folder ? folder : null,
            FeatureId = registration.Feature.Id
        };
    }

    private static void AddClient(
        Dictionary<CloudAccessScope, CloudOAuthClient> clients,
        CloudAccessScope scope,
        AppDriveOAuthClientManifest? declared,
        string? overrideClientId)
    {
        if (declared is null)
        {
            return;
        }

        string? clientId = overrideClientId ?? declared.ClientId?.Trim();
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            clients[scope] = new CloudOAuthClient(clientId, string.IsNullOrWhiteSpace(declared.Scope) ? null : declared.Scope);
        }
    }

    private static string? Override(LayeredConfigSnapshot? snapshot, string type, string key) =>
        snapshot is not null
        && snapshot.Values.TryGetValue($"{type}.{key}", out string? value)
        && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static Uri? HttpsAddress(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? address) && address.Scheme == Uri.UriSchemeHttps ? address : null;
}
