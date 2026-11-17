using Kazo.Os.App.Abstractions;

namespace Contoso.Sdk35DriveProvider;

/// <summary>
/// The manifest of the fixture Feature (ADR 0282 §1): it provides the <c>contoso-memory</c> drive
/// type, connected by a form (a space name and a secret passphrase). The build writes
/// <c>manifest.json</c> from this class.
/// </summary>
[AppManifest]
public sealed class MemoryDriveFeature : FeatureDefinition
{
    /// <summary>The Feature's id.</summary>
    public const string AppId = "com.contoso.sdk35-drive-provider";

    /// <summary>The filesystem type it provides.</summary>
    public const string DriveType = "contoso-memory";

    /// <summary>The name of the form field that chooses the space.</summary>
    public const string SpaceField = "space";

    /// <summary>The name of the secret form field.</summary>
    public const string PassphraseField = "passphrase";

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => "Contoso memory drives";

    /// <inheritdoc />
    public override string PublisherId => "pub.contoso";

    /// <inheritdoc />
    public override string Description => "SDK 3.5 compatibility fixture: a network drive type kept in memory.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];

    /// <inheritdoc />
    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    /// <inheritdoc />
    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new(DriveType, "Contoso memory", typeof(MemoryDriveProvider), AppDriveConnectionManifest.WithForm(
            new AppDriveFormFieldManifest(SpaceField, "Space", AppDriveFormFieldKind.Text),
            new AppDriveFormFieldManifest(PassphraseField, "Passphrase", AppDriveFormFieldKind.Text) { Secret = true })),
    ];
}
