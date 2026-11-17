using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// One type of network drive a feature provides (ADR 0282 §1), as <c>manifest.json</c> writes it in
/// <c>driveProviders</c>: the type <c>mount -t</c> shows, the name the user reads, the class of the
/// feature that talks to the service, how the user signs in, and the hosts the class reaches.
/// </summary>
/// <param name="Type">
/// The filesystem type (<c>exemple</c>, <c>webdav</c>): lowercase letters, digits and dashes, as
/// <c>mount -t</c>, <c>/proc/mounts</c> and <c>df -T</c> show it. The types of the OS are reserved
/// (<see cref="AppDriveProviders.ReservedTypes"/>).
/// </param>
/// <param name="Name">The name of the service, as the connect dialog and Settings show it (<c>Exemple</c>).</param>
/// <param name="Provider">
/// The class of the feature that derives from <c>Kazo.Os.AppSdk.Drives.KazoDriveProvider</c>,
/// written <c>Full.Type.Name, Assembly</c> like a provided service (ADR 0262).
/// </param>
/// <param name="Connection">How the user connects an account or a drive of this type (ADR 0282 §3).</param>
public sealed record AppDriveProviderManifest(
    string Type,
    string Name,
    string Provider,
    AppDriveConnectionManifest Connection)
{
    /// <summary>
    /// Gets the package-relative path of the service's icon, declared in the feature's assets, or
    /// <see langword="null"/> for the OS's generic network drive icon.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Gets the host names the provider's class reaches (ADR 0282 §4): an exact name
    /// (<c>api.example.com</c>) or a subdomain wildcard (<c>*.example.com</c>), always over <c>https</c>.
    /// A bare <c>*</c> is refused. The approval window names them.
    /// </summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>Gets the full name of the provider's class, e.g. <c>Exemple.ExempleDriveProvider</c>.</summary>
    [JsonIgnore]
    public string ProviderType => AppProvidedServiceManifest.TryParseName(Provider, out string typeName, out _) ? typeName : Provider;

    /// <summary>Gets the simple assembly name of the provider's class, e.g. <c>Exemple</c>.</summary>
    [JsonIgnore]
    public string ProviderAssembly => AppProvidedServiceManifest.TryParseName(Provider, out _, out string assemblyName) ? assemblyName : string.Empty;
}

/// <summary>How the user connects a drive of a type a feature provides (ADR 0282 §3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppDriveConnectionKind>))]
public enum AppDriveConnectionKind
{
    /// <summary>
    /// The OS signs the user in with OAuth 2.0 and PKCE, in its own window, keeps the refresh token
    /// sealed on the device (ADR 0269), and gives the provider an access token only.
    /// </summary>
    [JsonStringEnumMemberName("oauth")]
    OAuth,

    /// <summary>
    /// The OS draws a form of typed fields, seals the secret ones, and gives the provider the
    /// values of the drive it opens (the address, bucket and keys of S3, say).
    /// </summary>
    [JsonStringEnumMemberName("form")]
    Form
}

/// <summary>
/// The connection a drive provider declares (ADR 0282 §3): <see cref="AppDriveConnectionKind.OAuth"/>
/// with its <see cref="OAuth"/> addresses, or <see cref="AppDriveConnectionKind.Form"/> with its
/// <see cref="Fields"/>. Build one with <see cref="WithOAuth"/> or <see cref="WithForm"/>.
/// </summary>
/// <param name="Kind">The form of the connection.</param>
public sealed record AppDriveConnectionManifest(AppDriveConnectionKind Kind)
{
    /// <summary>Gets the OAuth addresses and app ids; set for <see cref="AppDriveConnectionKind.OAuth"/> only.</summary>
    [JsonPropertyName("oauth")]
    public AppDriveOAuthManifest? OAuth { get; init; }

    /// <summary>
    /// Gets the fields the user fills in, in display order; required for
    /// <see cref="AppDriveConnectionKind.Form"/>, empty for <see cref="AppDriveConnectionKind.OAuth"/>.
    /// </summary>
    public IReadOnlyList<AppDriveFormFieldManifest> Fields { get; init; } = [];

    /// <summary>Creates an OAuth connection: the OS runs the whole sign-in (ADR 0282 §3).</summary>
    /// <param name="oauth">The service's addresses and the provider's public app ids.</param>
    /// <returns>The connection.</returns>
    public static AppDriveConnectionManifest WithOAuth(AppDriveOAuthManifest oauth)
    {
        ArgumentNullException.ThrowIfNull(oauth);
        return new(AppDriveConnectionKind.OAuth) { OAuth = oauth };
    }

    /// <summary>Creates a form connection: the OS draws the fields and seals the secret ones (ADR 0282 §3).</summary>
    /// <param name="fields">The fields, in display order.</param>
    /// <returns>The connection.</returns>
    public static AppDriveConnectionManifest WithForm(params AppDriveFormFieldManifest[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new(AppDriveConnectionKind.Form) { Fields = [.. fields] };
    }
}

/// <summary>
/// The OAuth 2.0 side of a drive provider (ADR 0282 §3): what the shipped connectors keep in
/// <c>CloudOAuthProvider</c>, written as manifest data. Every address is absolute <c>https</c>. No
/// client secret exists: the app ids are public and the OS uses PKCE.
/// </summary>
/// <param name="AuthorizationEndpoint">The page the OS opens in the sign-in window.</param>
/// <param name="TokenEndpoint">Where the OS exchanges the code and renews the access token; it must accept CORS.</param>
public sealed record AppDriveOAuthManifest(string AuthorizationEndpoint, string TokenEndpoint)
{
    /// <summary>Gets where the OS revokes a token when the user removes the account, or <see langword="null"/> when the service has none for public clients.</summary>
    public string? RevocationEndpoint { get; init; }

    /// <summary>
    /// Gets the app id and scope of the access to all of the user's storage, or <see langword="null"/>
    /// when the provider offers none. At least one of <see cref="FullAccess"/> and <see cref="AppFolder"/> is set.
    /// </summary>
    public AppDriveOAuthClientManifest? FullAccess { get; init; }

    /// <summary>Gets the app id and scope of the access to a folder reserved for the app, or <see langword="null"/> when the provider offers none.</summary>
    public AppDriveOAuthClientManifest? AppFolder { get; init; }

    /// <summary>Gets where the service puts the reserved folder, as the user sees it in the service (<c>Apps/Kazo OS</c>), or <see langword="null"/>.</summary>
    public string? AppFolderPath { get; init; }

    /// <summary>Gets the service's own parameters of the authorization request (<c>token_access_type=offline</c>), or <see langword="null"/> for none.</summary>
    public IReadOnlyDictionary<string, string>? ExtraAuthorizationParameters { get; init; }

    /// <summary>Gets where the OS reads the signed-in account's id and name, or <see langword="null"/> when the service has no such address.</summary>
    public AppDriveIdentityManifest? Identity { get; init; }
}

/// <summary>One public OAuth app id of a drive provider and the scope string sent with it.</summary>
/// <param name="ClientId">The app id registered with the service; public, never a secret. An administrator may replace it per site (ADR 0282 §3).</param>
public sealed record AppDriveOAuthClientManifest(string ClientId)
{
    /// <summary>Gets the OAuth <c>scope</c> parameter, or <see langword="null"/> when the app's registration fixes it.</summary>
    public string? Scope { get; init; }
}

/// <summary>Where the OS asks the service who the signed-in account is, with the access token.</summary>
/// <param name="Address">The absolute <c>https</c> address.</param>
/// <param name="IdPath">Dotted JSON path of the account id in the answer (<c>account_id</c>).</param>
public sealed record AppDriveIdentityManifest(string Address, string IdPath)
{
    /// <summary>Gets whether the address is called by POST without a body (an RPC endpoint) instead of GET.</summary>
    public bool UsePost { get; init; }

    /// <summary>Gets the dotted JSON paths of a display name, tried in order.</summary>
    public IReadOnlyList<string> NamePaths { get; init; } = [];
}

/// <summary>The type of a field of a form connection (ADR 0282 §3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppDriveFormFieldKind>))]
public enum AppDriveFormFieldKind
{
    /// <summary>One line of text.</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>An absolute <c>https</c> address.</summary>
    [JsonStringEnumMemberName("url")]
    Url,

    /// <summary>A whole number.</summary>
    [JsonStringEnumMemberName("number")]
    Number,

    /// <summary>One value of <see cref="AppDriveFormFieldManifest.Choices"/>.</summary>
    [JsonStringEnumMemberName("choice")]
    Choice
}

/// <summary>One field of a form connection (ADR 0282 §3): the OS draws it, checks it and seals it when secret.</summary>
/// <param name="Name">The key the provider reads the value by (<c>bucket</c>, <c>access-key-id</c>): lowercase letters, digits and dashes.</param>
/// <param name="Label">The label the user reads.</param>
/// <param name="Kind">The type of the value.</param>
public sealed record AppDriveFormFieldManifest(string Name, string Label, AppDriveFormFieldKind Kind)
{
    /// <summary>
    /// Gets whether the value is a secret (a key, a password): the OS masks it, seals it in the
    /// user's keyring, never writes it to the drive list, and gives it only to the provider of the
    /// drive it belongs to (ADR 0269, ADR 0282 §3).
    /// </summary>
    public bool Secret { get; init; }

    /// <summary>Gets whether the user must fill the field in. <see langword="true"/> by default.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Gets the value shown at first, or <see langword="null"/>; never set on a secret field.</summary>
    public string? Default { get; init; }

    /// <summary>Gets the allowed values of a <see cref="AppDriveFormFieldKind.Choice"/> field, in display order.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>
    /// Gets whether the field holds the address of the server the user connects to (ADR 0284 §2),
    /// for a service with no host known in advance (WebDAV, a NAS). While a drive of the type is
    /// open, the OS allows the host of the address the user typed to the feature that provides the
    /// type, and the connection dialog writes it in plain words. Only on a required, non-secret
    /// <see cref="AppDriveFormFieldKind.Url"/> field, and on one field of a provider at most.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ServerAddress { get; init; }
}
