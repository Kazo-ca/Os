using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Testing.Drives;

namespace Kazo.Os.Template.DriveProvider.Tests;

/// <summary>
/// The provider passes the public conformance suite of Kazo OS (ADR 0282): the same cases the OS
/// relies on, against the in-memory service. Keep it green when you replace the service.
/// </summary>
public sealed class CloudDriveConformanceTests : KazoDriveProviderTests
{
    private readonly InMemoryCloudDrive _service = new();

    public static TheoryData<string> Cases => new(CaseNames);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_provider_conforms(string name)
    {
        KazoDriveConformanceResult result = await RunAsync(name);

        Assert.True(result.Outcome == KazoDriveConformanceOutcome.Passed, $"{name}: {result.Outcome} {result.Detail}");
    }

    protected override ValueTask<KazoDriveFolder> OpenDriveAsync(CancellationToken cancellationToken) =>
        new CloudDriveProvider(_service.Connect).OpenAsync(
            new KazoDriveOpenRequest("Template Drive", new KazoDriveTestContext(CloudDriveFeature.DriveType) { AccessToken = "token-1" }),
            cancellationToken);

    protected override ValueTask<bool> SetServiceReachableAsync(bool reachable, CancellationToken cancellationToken)
    {
        _service.Online = reachable;
        return ValueTask.FromResult(true);
    }

    protected override ValueTask<bool> ExpireSignInAsync(CancellationToken cancellationToken)
    {
        _service.ValidToken = "token-revoked-by-the-service";
        return ValueTask.FromResult(true);
    }

    protected override ValueTask<bool> PlaceLargeFileAsync(KazoDrivePath path, long length, CancellationToken cancellationToken)
    {
        _service.PlaceFile("/" + string.Join('/', path.Segments), length);
        return ValueTask.FromResult(true);
    }

    protected override ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        _service.Online = true;
        _service.ValidToken = "token-1";
        return ValueTask.CompletedTask;
    }
}

/// <summary>What the suite does not see: the manifest the build writes.</summary>
public sealed class CloudDriveFeatureTests
{
    [Fact]
    public void The_manifest_is_valid_and_names_the_api_host()
    {
        AppManifest feature = AppDefinitionMapper.ToFeatureManifests(new CloudDriveFeature(), KazoOsSdk.Version.ToString()).Feature;

        Assert.Empty(AppManifestValidator.Validate(feature).Errors);
        Assert.Contains(AppCapabilities.DrivesProvide, feature.Capabilities);
        Assert.Equal([CloudDriveFeature.ApiHost], AppDriveProviders.HostsOf(feature));
    }

    [Fact]
    public async Task Opening_a_drive_sends_nothing()
    {
        InMemoryCloudDrive service = new() { Online = false };
        KazoDriveTestContext context = new(CloudDriveFeature.DriveType) { AccessToken = "token-1" };

        await using KazoDriveFolder drive = await new CloudDriveProvider(service.Connect).OpenAsync(new KazoDriveOpenRequest("Template Drive", context));

        Assert.Equal(0, context.TokenRequests);
        Assert.Equal(KazoDriveState.Unavailable, await drive.GetStateAsync());
    }
}
