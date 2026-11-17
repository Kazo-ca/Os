using Kazo.Os.AppSdk.CloudAccounts;

namespace Kazo.Os.AppSdk.IO;

/// <summary>How the user connects a drive of one network drive type (ADR 0282 §5).</summary>
public enum KazoNetworkDriveConnection
{
    /// <summary>A folder of the computer, chosen in the browser's folder dialog (<c>hostfs</c>, ADR 0181).</summary>
    HostFolder,

    /// <summary>
    /// A sign-in to the service in its own window: <see cref="KazoCloudAccounts.ConnectAsync"/> with one of
    /// <see cref="KazoNetworkDriveTypeInfo.Scopes"/>, then <see cref="KazoDriveInfo.MountNetworkDriveAsync"/>
    /// with the new account.
    /// </summary>
    OAuth,

    /// <summary>
    /// A form of <see cref="KazoNetworkDriveTypeInfo.Fields"/>: <see cref="KazoDriveInfo.MountNetworkDriveAsync"/>
    /// with the values in <see cref="KazoNetworkDriveRequest.Options"/> and the secret ones in
    /// <see cref="KazoNetworkDriveRequest.Secrets"/>.
    /// </summary>
    Form
}

/// <summary>What one field of a connection form holds (ADR 0282 §2).</summary>
public enum KazoNetworkDriveFieldKind
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>An address.</summary>
    Url,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary>One of <see cref="KazoNetworkDriveField.Choices"/>.</summary>
    Choice,

    /// <summary>A secret the OS seals on the device and never shows again: a masked field, never read from a command line.</summary>
    Secret
}

/// <summary>One choice of a <see cref="KazoNetworkDriveFieldKind.Choice"/> field.</summary>
/// <param name="Value">What goes in the request; may be empty (the service's default).</param>
/// <param name="Label">What the user sees.</param>
public sealed record KazoNetworkDriveFieldChoice(string Value, string Label);

/// <summary>One field of the form of a network drive type (ADR 0282 §2, §5).</summary>
/// <param name="Name">The key of the value in the request, as <c>mount -o</c> takes it, e.g. <c>endpoint</c>.</param>
/// <param name="Label">What the user sees: in the asked language for the OS's own types, as the feature wrote it otherwise.</param>
/// <param name="Kind">What the field holds.</param>
public sealed record KazoNetworkDriveField(string Name, string Label, KazoNetworkDriveFieldKind Kind)
{
    /// <summary>Gets whether the drive cannot be mounted without this field; <see langword="true"/> unless set.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Gets the value the field starts with; <see langword="null"/> for none.</summary>
    public string? Default { get; init; }

    /// <summary>Gets the hint shown in the empty field; <see langword="null"/> for none.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Gets the choices of a <see cref="KazoNetworkDriveFieldKind.Choice"/> field; empty otherwise.</summary>
    public IReadOnlyList<KazoNetworkDriveFieldChoice> Choices { get; init; } = [];

    /// <summary>
    /// Gets whether the field holds the address of the server the provider reaches (ADR 0284 §2): the
    /// OS allows the host typed there to the providing feature while the drive is open, so the connect
    /// dialog writes that host before saving. At most one field of a type, always an address.
    /// </summary>
    public bool IsServerAddress { get; init; }

    /// <summary>Gets whether the value goes in <see cref="KazoNetworkDriveRequest.Secrets"/> rather than in <see cref="KazoNetworkDriveRequest.Options"/>.</summary>
    public bool IsSecret => Kind == KazoNetworkDriveFieldKind.Secret;
}

/// <summary>
/// One type of network drive this OS can mount now, as the Explorer, Settings and <c>mount -t</c> show
/// it (ADR 0282 §5): its name, its icon, how the user connects it, and the feature that provides it
/// when it is not one of the OS's own types. Read with <see cref="KazoDriveInfo.DescribeNetworkDriveTypesAsync"/>.
/// </summary>
/// <param name="Type">The type, as <c>mount -t</c> takes it: <c>hostfs</c>, <c>dropbox</c>, <c>s3</c>, <c>pcloud</c>…</param>
/// <param name="DisplayName">The name of the service as its provider writes it, or of a folder of the computer in the asked language.</param>
/// <param name="Connection">How the user connects a drive of this type.</param>
public sealed record KazoNetworkDriveTypeInfo(string Type, string DisplayName, KazoNetworkDriveConnection Connection)
{
    /// <summary>
    /// Gets the icon glyph of the type as <c>&lt;library&gt;:&lt;name&gt;</c> of the SDK's icon fonts, e.g.
    /// <c>bootstrap:dropbox</c> or <c>lucide:database</c>; <see langword="null"/> when none is known.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Gets the page-relative address of the image a feature declares for its type
    /// (<c>_content/&lt;assembly&gt;/&lt;path&gt;</c>); <see langword="null"/> for the OS's own types.
    /// </summary>
    public string? IconUrl { get; init; }

    /// <summary>Gets the scopes the user can choose for an <see cref="KazoNetworkDriveConnection.OAuth"/> type; empty otherwise.</summary>
    public IReadOnlyList<KazoCloudAccessScope> Scopes { get; init; } = [];

    /// <summary>Gets where the service puts the folder reserved for Kazo OS, as in <see cref="KazoCloudProvider.AppFolderPath"/>; <see langword="null"/> when unknown.</summary>
    public string? AppFolderPath { get; init; }

    /// <summary>Gets the fields of a <see cref="KazoNetworkDriveConnection.Form"/> type, in the order to show them; empty otherwise.</summary>
    public IReadOnlyList<KazoNetworkDriveField> Fields { get; init; } = [];

    /// <summary>
    /// Gets the id of the feature that provides the type (ADR 0282 §1); <see langword="null"/> for the OS's
    /// own types. The OS always shows it next to the service's name, so a feature cannot pass for another service.
    /// </summary>
    public string? ProviderFeatureId { get; init; }

    /// <summary>Gets the name of the feature that provides the type; <see langword="null"/> for the OS's own types.</summary>
    public string? ProviderFeatureName { get; init; }

    /// <summary>Gets whether a feature provides the type rather than the OS.</summary>
    public bool IsThirdParty => ProviderFeatureId is not null;
}

/// <summary>Why a network drive is kept without being mounted (ADR 0282 §3, §5); see <see cref="KazoDriveInfo.ProviderMissing"/>.</summary>
public enum KazoDriveProviderMissingReason
{
    /// <summary>The drive is mounted: its type has a provider.</summary>
    None,

    /// <summary>The feature that provided the type is no longer installed: the Store can install it again.</summary>
    Removed,

    /// <summary>The feature that provided the type is installed but disabled.</summary>
    Disabled,

    /// <summary>The type is one of the OS's own, switched off by the administrator of this OS.</summary>
    DisabledByAdministrator
}
