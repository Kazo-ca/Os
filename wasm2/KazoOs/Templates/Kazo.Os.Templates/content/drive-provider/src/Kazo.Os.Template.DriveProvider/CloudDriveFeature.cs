using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Template.DriveProvider;

/// <summary>
/// The manifest of the Feature (ADR 0282 §1): it provides the <c>template-drive</c> type of network
/// drive. The build writes <c>manifest.json</c> from this class; it is never instantiated at run
/// time. An administrator installs the Feature for the machine; each user then connects their own
/// account in "Connect a network drive…", Settings or <c>mount -t template-drive</c>.
/// </summary>
[AppManifest]
public sealed class CloudDriveFeature : FeatureDefinition
{
    /// <summary>The Feature's id.</summary>
    public const string AppId = "com.example.template-app";

    /// <summary>The filesystem type it provides, as <c>mount -t</c>, <c>/proc/mounts</c> and <c>df -T</c> show it.</summary>
    public const string DriveType = "template-drive";

    /// <summary>The host of the service's API; <c>drives.provide</c> lets the provider reach it (ADR 0284 §1).</summary>
    public const string ApiHost = "api.template-drive.example";

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => "Template Drive";

    /// <inheritdoc />
    public override string PublisherId => "pub.template";

    /// <inheritdoc />
    public override string Description => "Mounts a Template Drive account as a network drive.";

    /// <inheritdoc />
    /// <remarks>
    /// <c>drives.provide</c> is a dangerous capability: the administrator who installs the Feature
    /// approves it, and the approval screen names the hosts below. It is enough for them: no
    /// <c>network.direct.access</c> is needed (ADR 0284 §1).
    /// </remarks>
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];

    /// <inheritdoc />
    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    /// <inheritdoc />
    /// <remarks>
    /// The OS runs the whole OAuth sign-in with PKCE in its own window, keeps the refresh token
    /// sealed on the device, and lends the provider an access token only (ADR 0282 §3). Register
    /// the OS's return page with the service (see the developer guide, "OAuth return page") and
    /// replace the addresses and the public app id. A service without PKCE cannot use this: use a
    /// form connection (<see cref="AppDriveConnectionManifest.WithForm"/>) instead.
    /// </remarks>
    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new(DriveType, "Template Drive", typeof(CloudDriveProvider), AppDriveConnectionManifest.WithOAuth(
            new AppDriveOAuthManifest(
                "https://" + ApiHost + "/oauth2/authorize",
                "https://" + ApiHost + "/oauth2/token")
            {
                FullAccess = new("replace-with-your-public-app-id") { Scope = "files.read files.write" },
                Identity = new("https://" + ApiHost + "/v1/me", "id") { NamePaths = ["email", "name"] },
            }))
        {
            Hosts = [ApiHost],
        },
    ];
}
