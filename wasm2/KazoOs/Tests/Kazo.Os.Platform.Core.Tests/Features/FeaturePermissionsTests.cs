using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Features;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Policy;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>
/// The permissions a feature declares (ADR 0246): known to the OS while the feature is installed and
/// enabled, granted by the same rules as the built-in catalog, never silently.
/// </summary>
public sealed partial class FeaturePermissionsTests
{
    private const string User = "user-1";

    private static CapabilityGrantRepository Grants() => new();

    [Fact]
    public void The_catalog_knows_the_permissions_of_an_installed_enabled_feature_only()
    {
        AppCatalog catalog = FeaturePermissionFixtures.Catalog().CreateLiveCopy();
        AppEnablementRegistry enablement = new(catalog);
        using InstalledCapabilityCatalog capabilities = new(catalog, enablement);

        CapabilityDescription destroy = capabilities.Find(FeaturePermissionFixtures.Destroy)!;
        Assert.Equal((CapabilityRisk.Dangerous, FeaturePermissionFixtures.FeatureId), (destroy.Risk, destroy.ProviderId));
        Assert.Equal("The app can delete a machine and its disk.", destroy.RiskDescription);
        Assert.True(capabilities.IsNormal(FeaturePermissionFixtures.Use));
        Assert.Equal([FeaturePermissionFixtures.Destroy, FeaturePermissionFixtures.Use], capabilities.GetCovered(FeaturePermissionFixtures.All).Order(StringComparer.Ordinal));

        enablement.MarkDisabled([FeaturePermissionFixtures.FeatureId]);
        Assert.Null(capabilities.Find(FeaturePermissionFixtures.Destroy));

        enablement.MarkEnabled(FeaturePermissionFixtures.FeatureId);
        catalog.ReplaceContents(FeaturePermissionFixtures.Catalog(withFeature: false));
        Assert.Null(capabilities.Find(FeaturePermissionFixtures.Use));
        Assert.True(capabilities.IsKnown(AppCapabilities.ClipboardRead));
    }

    [Fact]
    public void A_normal_permission_is_granted_at_sign_in_only_while_its_feature_is_installed()
    {
        AppManifest client = FeaturePermissionFixtures.Client();
        CapabilityGrantRepository without = Grants();
        CapabilityGrantRepository with = Grants();
        List<string> skipped = [];

        CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(without, client, User, false, BuiltInCapabilityCatalog.Instance, skipped.Add);
        CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(with, client, User, false, new FeatureCapabilityCatalog(BuiltInCapabilityCatalog.Instance, [FeaturePermissionFixtures.Feature()]));

        Assert.False(Evaluate(without, FeaturePermissionFixtures.Use));
        Assert.Equal([FeaturePermissionFixtures.Use, FeaturePermissionFixtures.Destroy], skipped);
        Assert.True(Evaluate(with, FeaturePermissionFixtures.Use));
        // ADR 0246 §3: a dangerous permission waits for an administrator, as in the built-in catalog.
        Assert.False(Evaluate(with, FeaturePermissionFixtures.Destroy));
    }

    [Fact]
    public void An_unknown_identifier_and_a_topic_permission_are_never_granted_silently()
    {
        // Issue #432: before ADR 0246 the seeder granted anything outside the catalog.
        const string topic = "topic-publish:app/org.test.other/events";
        AppManifest manifest = FeaturePermissionFixtures.Client() with { Capabilities = ["machine.anything.random", topic, AppCapabilities.ClipboardRead] };
        CapabilityGrantRepository grants = Grants();
        List<string> skipped = [];

        CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(grants, manifest, User, false, BuiltInCapabilityCatalog.Instance, skipped.Add);

        Assert.Equal(["machine.anything.random"], skipped);
        Assert.False(Evaluate(grants, "machine.anything.random"));
        Assert.False(Evaluate(grants, topic));
        Assert.True(Evaluate(grants, AppCapabilities.ClipboardRead));
        Assert.True(BuiltInCapabilityCatalog.Instance.IsDangerous(topic));
    }

    private static bool Evaluate(CapabilityGrantRepository grants, string capability) =>
        grants.Evaluate(FeaturePermissionFixtures.ClientId, User, capability, AppAuthority.User, AppAuthority.User).Granted;

    private static AppCapabilityChecker Checker(CapabilityGrantRepository grants, ICapabilityCatalog catalog) =>
        new(grants, FeaturePermissionFixtures.ClientId, User, AppAuthority.User) { Capabilities = catalog };

    private static void Grant(CapabilityGrantRepository grants, string capability) =>
        grants.Grant(FeaturePermissionFixtures.ClientId, User, capability, CapabilityGrantSource.AdministratorApproval, AppAuthority.System);
}
