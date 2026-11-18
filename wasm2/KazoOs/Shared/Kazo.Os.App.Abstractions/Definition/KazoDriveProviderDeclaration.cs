namespace Kazo.Os.App.Abstractions;

/// <summary>
/// One type of network drive a <see cref="FeatureDefinition"/> provides (ADR 0282 §1), as its C#
/// source declares it in <see cref="FeatureDefinition.DriveProviders"/>. The manifest generator
/// writes it to <c>driveProviders</c> as an <see cref="AppDriveProviderManifest"/>.
/// </summary>
/// <param name="Type">
/// The filesystem type (<c>exemple</c>): lowercase letters, digits and dashes, not one of
/// <see cref="AppDriveProviders.ReservedTypes"/>. One feature provides a type; a second feature that
/// declares the same type is refused at installation.
/// </param>
/// <param name="Name">The name of the service the user reads (<c>Exemple</c>). The OS always shows the feature's name next to it.</param>
/// <param name="Provider">
/// The class of the feature's assembly that derives from <c>Kazo.Os.AppSdk.Drives.KazoDriveProvider</c>,
/// concrete, with a public constructor taking an <c>IFeatureServiceContext</c> or none.
/// </param>
/// <param name="Connection">
/// How the user connects (ADR 0282 §3): <see cref="AppDriveConnectionManifest.WithOAuth"/> or
/// <see cref="AppDriveConnectionManifest.WithForm"/>. The OS runs it; the provider never sees a refresh token.
/// </param>
public sealed record KazoDriveProviderDeclaration(
    string Type,
    string Name,
    Type Provider,
    AppDriveConnectionManifest Connection)
{
    /// <summary>Gets the package-relative path of the service's icon, declared in the feature's <see cref="AppDefinition.Assets"/>, or <see langword="null"/>.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Gets the hosts the provider reaches over <c>https</c> (ADR 0282 §4): <c>api.example.com</c> or
    /// <c>*.example.com</c>. The OS refuses any other host to the provider's direct HTTP calls, and
    /// the approval window names them.
    /// </summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];
}
