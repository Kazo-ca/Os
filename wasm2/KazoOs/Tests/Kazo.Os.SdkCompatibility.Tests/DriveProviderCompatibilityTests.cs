using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Testing.Drives;

namespace Kazo.Os.SdkCompatibility.Tests;

/// <summary>
/// ADR 0282 (series <c>drive-providers</c>, work item 9): a third-party Feature compiled once
/// against SDK 3.5.0, the first with network drive providers (<c>Contoso.Sdk35DriveProvider</c>),
/// still declares its drive type to the current OS and still passes the public conformance suite.
/// The generic checks of <see cref="SdkCompatibilityTests"/> (catalog, references, binding) run on
/// it too.
/// </summary>
public sealed class DriveProviderCompatibilityTests
{
    /// <summary>The fixture package compiled against SDK 3.5.0.</summary>
    internal const string Package = "Contoso.Sdk35DriveProvider.3.5.0.nupkg";

    /// <summary>The drive type the fixture declares.</summary>
    internal const string DriveType = "contoso-memory";

    [Fact]
    public void The_SDK_3_5_0_feature_declares_its_drive_type_with_a_form()
    {
        FixturePackage fixture = FixturePackage.Open(Package);

        AppManifest feature = Assert.Single(fixture.Manifests);
        Assert.Equal(AppKind.Feature, feature.Kind);
        Assert.Equal("3.5.0", feature.SdkCompatibility!.MinimumVersion);
        Assert.Contains(AppCapabilities.DrivesProvide, feature.Capabilities);
        Assert.Empty(AppManifestValidator.Validate(feature).Errors);
        AppDriveProviderManifest provider = Assert.Single(feature.DriveProviders!);
        Assert.Equal(DriveType, provider.Type);
        Assert.Equal(["space", "passphrase"], provider.Connection.Fields!.Select(static field => field.Name));
        Assert.True(provider.Connection.Fields![1].Secret);
        Assert.NotNull(fixture.Assembly.GetType(provider.Provider.Split(',')[0].Trim(), throwOnError: false));
    }

    public static TheoryData<string> Cases => new(KazoDriveProviderTests.CaseNames);

    /// <summary>
    /// The provider of the 3.5.0 Feature, created the way the OS creates it (its public
    /// parameterless constructor) and opened with a form connection, passes every case of the
    /// current suite: a create, a read, a write, a revision conflict, no network, a refused
    /// passphrase, a file too large, refused names.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_SDK_3_5_0_provider_passes_the_current_conformance_suite(string name)
    {
        KazoDriveConformanceResult result = await new FixtureProviderConformance().RunAsync(name);

        Assert.True(result.Outcome == KazoDriveConformanceOutcome.Passed, $"{name}: {result.Outcome} {result.Detail}");
    }

    /// <summary>
    /// The suite run against the provider of the fixture. The fixture's assembly lives in a load
    /// context of its own, so its in-memory service is driven by reflection through its public
    /// <c>MemorySpaces</c> class; each instance uses a space of its own.
    /// </summary>
    private sealed class FixtureProviderConformance : KazoDriveProviderTests
    {
        private const string Passphrase = "passphrase-1";

        private static readonly Lazy<FixturePackage> Fixture = new(() => FixturePackage.Open(Package));

        private readonly string _space = "conformance-" + Guid.NewGuid().ToString("N")[..12];

        protected override ValueTask<KazoDriveFolder> OpenDriveAsync(CancellationToken cancellationToken)
        {
            AppDriveProviderManifest declaration = Fixture.Value.Manifests.Single().DriveProviders!.Single();
            Type type = Fixture.Value.Assembly.GetType(declaration.Provider.Split(',')[0].Trim(), throwOnError: true)!;
            KazoDriveProvider provider = (KazoDriveProvider)Activator.CreateInstance(type)!;
            KazoDriveTestContext context = new(DriveType);
            context.Secrets["passphrase"] = Passphrase;
            return provider.OpenAsync(
                new KazoDriveOpenRequest("Contoso memory", context)
                {
                    Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["space"] = _space },
                },
                cancellationToken);
        }

        protected override ValueTask<bool> SetServiceReachableAsync(bool reachable, CancellationToken cancellationToken)
        {
            Call("SetOnline", _space, reachable);
            return ValueTask.FromResult(true);
        }

        protected override ValueTask<bool> ExpireSignInAsync(CancellationToken cancellationToken)
        {
            Call("SetPassphrase", _space, "passphrase-revoked-by-the-service");
            return ValueTask.FromResult(true);
        }

        protected override ValueTask<bool> PlaceLargeFileAsync(KazoDrivePath path, long length, CancellationToken cancellationToken)
        {
            Call("PlaceFile", _space, "/" + string.Join('/', path.Segments), length);
            return ValueTask.FromResult(true);
        }

        protected override ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            Call("SetOnline", _space, true);
            Call("SetPassphrase", _space, Passphrase);
            return ValueTask.CompletedTask;
        }

        /// <summary>Calls a public static method of the fixture's <c>MemorySpaces</c> class.</summary>
        private static void Call(string method, params object[] arguments)
        {
            Type spaces = Fixture.Value.Assembly.GetType("Contoso.Sdk35DriveProvider.MemorySpaces", throwOnError: true)!;
            MethodInfo target = spaces.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(spaces.FullName, method);
            target.Invoke(null, arguments);
        }
    }
}
