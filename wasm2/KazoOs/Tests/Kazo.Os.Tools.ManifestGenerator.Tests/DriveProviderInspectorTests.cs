using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Features;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>The build checks of ADR 0282: a drive provider the OS can create (<c>KOSAPP0036</c>), reserved to a feature (<c>KOSAPP0033</c>).</summary>
public sealed class DriveProviderInspectorTests
{
    private static readonly Assembly Fixtures = typeof(ValidCommandApp).Assembly;

    private static InspectionResult Inspect(Type type) => AppDefinitionInspector.Inspect(Fixtures, [type], "1.0.0");

    [Fact]
    public void A_valid_provider_is_written_in_the_feature_manifest()
    {
        InspectionResult result = Inspect(typeof(DriveProvidingFeature));

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        AppDriveProviderManifest provider = Assert.Single(Assert.Single(result.Manifests).Manifest.DriveProviders!);
        Assert.Equal("fixture-drive", provider.Type);
        Assert.Equal(typeof(FixtureDriveProvider).FullName, provider.ProviderType);
        Assert.Equal(["api.example.org"], provider.Hosts);
    }

    [Theory]
    [InlineData(typeof(FeatureWithNonProvider), "does not derive from KazoDriveProvider")]
    [InlineData(typeof(FeatureWithAbstractProvider), "concrete class")]
    [InlineData(typeof(FeatureWithUncreatableDriveProvider), "public constructor")]
    public void KOSAPP0036_a_provider_the_os_cannot_create(Type feature, string reason)
    {
        InspectionResult result = Inspect(feature);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == GeneratorCodes.DriveProvider && diagnostic.Message.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_reserved_type_fails_the_build()
    {
        InspectionResult result = Inspect(typeof(FeatureWithReservedDriveType));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Message.Contains("onedrive", StringComparison.Ordinal));
    }

    [Fact]
    public void KOSAPP0033_an_ordinary_app_that_declares_drives_provide()
    {
        InspectionResult result = Inspect(typeof(OrdinaryAppProvidingDrives));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == GeneratorCodes.FeatureOnly);
    }
}

/// <summary>A valid drive provider: it derives from <see cref="KazoDriveProvider"/> and takes the feature's context.</summary>
public sealed class FixtureDriveProvider(IFeatureServiceContext context) : KazoDriveProvider
{
    public IFeatureServiceContext Context { get; } = context;

    public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default) =>
        throw new KazoDriveException(KazoDriveErrorKind.Unavailable, "Not used by the build checks.");
}

/// <summary>An abstract provider (<c>KOSAPP0036</c>).</summary>
public abstract class AbstractDriveProvider : KazoDriveProvider;

/// <summary>A provider the OS cannot create: its constructor asks for something else (<c>KOSAPP0036</c>).</summary>
public sealed class UncreatableDriveProvider(string name) : KazoDriveProvider
{
    public string Name { get; } = name;

    public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default) =>
        throw new KazoDriveException(KazoDriveErrorKind.Unavailable, Name);
}

/// <summary>A feature that provides one drive type (ADR 0282 §1).</summary>
public class DriveProvidingFeature : ValidFeature
{
    public override IReadOnlyList<string> Capabilities => [.. base.Capabilities, AppCapabilities.DrivesProvide];

    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new(DriveType, "Fixture drive", ProviderClass, AppDriveConnectionManifest.WithForm(
            new AppDriveFormFieldManifest("address", "Address", AppDriveFormFieldKind.Url)))
        {
            Hosts = ["api.example.org"],
        },
    ];

    protected virtual string DriveType => "fixture-drive";

    protected virtual Type ProviderClass => typeof(FixtureDriveProvider);
}

/// <summary>A class that is not a provider (<c>KOSAPP0036</c>).</summary>
public sealed class FeatureWithNonProvider : DriveProvidingFeature
{
    protected override Type ProviderClass => typeof(NotAProvider);
}

/// <summary>An abstract provider (<c>KOSAPP0036</c>).</summary>
public sealed class FeatureWithAbstractProvider : DriveProvidingFeature
{
    protected override Type ProviderClass => typeof(AbstractDriveProvider);
}

/// <summary>A provider without a usable constructor (<c>KOSAPP0036</c>).</summary>
public sealed class FeatureWithUncreatableDriveProvider : DriveProvidingFeature
{
    protected override Type ProviderClass => typeof(UncreatableDriveProvider);
}

/// <summary>A type of the OS (<c>manifest.drive-provider.reserved</c>).</summary>
public sealed class FeatureWithReservedDriveType : DriveProvidingFeature
{
    protected override string DriveType => "onedrive";
}

/// <summary>An ordinary app that declares the capability of a drive provider (<c>KOSAPP0033</c>).</summary>
public sealed class OrdinaryAppProvidingDrives : ValidCommandApp
{
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];
}
