using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Samples.WebDavDrive;

/// <summary>
/// Manifest of the sample feature that provides the <c>webdav</c> drive type (ADR 0282 §1). The
/// build generates <c>manifest.json</c> from this class; it is never instantiated at runtime. An
/// administrator installs it for the machine; each user then connects their own servers in
/// "Connect a network drive…", Settings or <c>mount -t webdav</c>.
/// </summary>
[AppManifest]
public sealed class WebDavFeature : FeatureDefinition
{
    /// <summary>The feature's id.</summary>
    public const string AppId = "org.kazoos.samples.webdav-drive";

    /// <summary>The filesystem type it provides, as <c>mount -t</c> shows it.</summary>
    public const string DriveType = "webdav";

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => "WebDAV drives";

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Mounts a WebDAV server (Nextcloud, ownCloud, Apache, a NAS) as a network drive.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];

    /// <inheritdoc />
    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    /// <inheritdoc />
    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new(DriveType, "WebDAV", typeof(WebDavDriveProvider), AppDriveConnectionManifest.WithForm(
            new AppDriveFormFieldManifest(WebDavFields.Server, "Server address", AppDriveFormFieldKind.Url),
            new AppDriveFormFieldManifest(WebDavFields.UserName, "User name", AppDriveFormFieldKind.Text),
            new AppDriveFormFieldManifest(WebDavFields.Password, "Password", AppDriveFormFieldKind.Text) { Secret = true })),
    ];
}
