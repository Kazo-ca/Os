using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Tests.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// ADR 0248: installing an app installs its missing required dependencies in one plan, one consent
/// and one transaction; removing a package disables the apps that need it, and installing it again
/// reactivates them; an update out of a dependent's range is refused. The case aimed at is an app
/// that needs a feature: here a client app of fixture and the feature of fixture.
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    private const string ClientId = "org.kazo.client";
    private const string NeedsFeature = "[{\"appId\":\"" + AppPackageTests.FeatureId + "\",\"minimumVersion\":\"1.0.0\",\"maximumVersion\":\"1.9.9\",\"optional\":false}]";
    private const string MayUseFeature = "[{\"appId\":\"" + AppPackageTests.FeatureId + "\",\"minimumVersion\":\"1.0.0\",\"maximumVersion\":null,\"optional\":true}]";

    [Fact]
    public async Task An_app_that_needs_an_absent_feature_installs_both_with_one_consent_the_feature_for_the_system()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", NeedsFeature), Harness.FeatureItem("1.0.0"));
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: approval);

        KazoPackageInstallPlan plan = await gateway.PlanPackageInstallAsync(ClientId);
        KazoPackageInstallReport report = await gateway.InstallPackageAsync(ClientId);

        // The plan: the feature first (ADR 0005), then the app; one administrator for the whole plan.
        Assert.Equal([AppPackageTests.FeatureId, ClientId], plan.Packages.Select(static package => package.AppId));
        Assert.True(plan.Packages[0].IsFeature);
        Assert.True(plan.Packages[0].SystemWide);
        Assert.False(plan.Packages[1].SystemWide);
        Assert.True(plan.Packages[1].Requested);
        Assert.True(plan.RequiresAdministrator);
        Assert.Equal(1, agent.Calls);
        AppInstallApprovalRequest request = Assert.Single(approval.Requests);
        Assert.Equal(ClientId, request.AppId);
        AppInstallApprovalDependency dependency = Assert.Single(request.Dependencies);
        Assert.Equal(AppPackageTests.FeatureId, dependency.AppId);
        Assert.True(dependency.IsFeature);
        Assert.True(dependency.SystemWide);
        // Answer 268 A: the window knows the feature of the plan and describes its permissions.
        Assert.Equal([AppPackageTests.FeatureId], request.PlannedFeatures.Select(static manifest => manifest.Id));
        string[] featureSet = ["clipboard.read", "filesystem.system.read"];
        Assert.Equal(
            featureSet.Where(BuiltInCapabilityCatalog.Instance.IsDangerous).Order(StringComparer.Ordinal),
            dependency.DangerousCapabilities.Order(StringComparer.Ordinal));

        // Answer 2 A: the app stays personal, its feature goes to the system.
        Assert.False(report.SystemWide);
        KazoPackageInstallReport installedFeature = Assert.Single(report.Dependencies);
        Assert.Equal(AppPackageTests.FeatureId, installedFeature.AppId);
        Assert.True(installedFeature.SystemWide);
        Assert.Contains(AppPackageTests.FeatureId + "=", await harness.Settings.ReadAsync("/etc/kazoos/apps.config") ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(ClientId + "=", await harness.Settings.ReadAsync(PersonalConfig) ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, AppInstallApprovalOutcome.Approved)]
    [InlineData(true, AppInstallApprovalOutcome.Refused)]
    public async Task A_user_who_cannot_approve_the_feature_installs_nothing(bool administratorSignsIn, AppInstallApprovalOutcome outcome)
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", NeedsFeature), Harness.FeatureItem("1.0.0"));
        CountingAgent agent = new(administratorSignsIn);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: new FakeApproval(outcome));

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => gateway.InstallPackageAsync(ClientId).AsTask());

        // ADR 0248 §1: never an app installed without its dependency.
        Assert.DoesNotContain(AppPackageTests.FeatureId, await harness.Settings.ReadAsync("/etc/kazoos/apps.config") ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientId, await harness.Settings.ReadAsync(PersonalConfig) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_dependency_in_no_catalog_stops_the_installation_with_its_id_and_range()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", NeedsFeature));
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        KazoPackageException refused = await Assert.ThrowsAsync<KazoPackageException>(() => gateway.InstallPackageAsync(ClientId).AsTask());

        Assert.Equal(AppPackageInstaller.DependencyUnresolvedCode, refused.Code);
        Assert.Contains(AppPackageTests.FeatureId, refused.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0 - 1.9.9", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, agent.Calls);
        Assert.DoesNotContain(ClientId, await harness.Settings.ReadAsync(PersonalConfig) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_optional_dependency_is_not_installed_with_the_app()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", MayUseFeature), Harness.FeatureItem("1.0.0"));
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        KazoPackageInstallReport report = await gateway.InstallPackageAsync(ClientId);

        // ADR 0248 §2: the app installs alone, for the user, without an administrator.
        Assert.Empty(report.Dependencies);
        Assert.False(report.SystemWide);
        Assert.Equal(0, agent.Calls);
        Assert.DoesNotContain(AppPackageTests.FeatureId, await harness.Settings.ReadAsync("/etc/kazoos/apps.config") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installed_dependency_out_of_range_is_updated_in_the_same_plan()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.FeatureItem("1.0.0"));
        AppManagementGateway first = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));
        await first.InstallPackageAsync(AppPackageTests.FeatureId, KazoPackageScope.System);
        harness.Now += TimeSpan.FromMinutes(10);
        const string needsNewer = "[{\"appId\":\"" + AppPackageTests.FeatureId + "\",\"minimumVersion\":\"2.0.0\",\"maximumVersion\":null,\"optional\":false}]";
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", needsNewer), Harness.FeatureItem("2.0.0", "1.0.0"));
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        KazoPackageInstallPlan plan = await gateway.PlanPackageInstallAsync(ClientId);

        // The version installed is out of the app's range: the plan brings the newest in range.
        KazoPlannedPackage feature = plan.Packages[0];
        Assert.Equal(AppPackageTests.FeatureId, feature.AppId);
        Assert.Equal("1.0.0", feature.PreviousVersion);
        Assert.Equal("2.0.0", feature.Version);
    }

    [Fact]
    public async Task Removing_a_feature_warns_disables_its_dependents_and_installing_it_again_reactivates_them()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", NeedsFeature), Harness.FeatureItem("1.0.0"));
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway installer = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: approval);
        await installer.InstallPackageAsync(ClientId);
        // The OS reloaded the installed apps: the feature and the app that needs it are in the catalog.
        harness.Catalog = Harness.CatalogWith(Harness.FeaturePackage("1.0.0"), Harness.ClientPackage("1.0.0", NeedsFeature));
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: approval);

        IReadOnlyList<KazoPackageDependent> dependents = await gateway.GetPackageDependentsAsync(AppPackageTests.FeatureId);
        KazoInstalledPackage removed = await gateway.RemovePackageAsync(AppPackageTests.FeatureId);

        Assert.Equal([ClientId], dependents.Select(static app => app.AppId));
        Assert.Equal(["Client"], approval.Requests[^1].DisabledDependents);
        Assert.Equal([ClientId], removed.DisabledDependents);
        string machine = await harness.Settings.ReadAsync("/etc/kazoos/apps.config") ?? string.Empty;
        Assert.Contains("[disabled-by-dependency]", machine, StringComparison.Ordinal);
        Assert.Single(AppsConfigDocument.ReadDisabledByDependency(machine, AppPackageTests.FeatureId), ClientId);
        // Disabled, not removed: its package stays.
        Assert.Contains(ClientId + "=", await harness.Settings.ReadAsync(PersonalConfig) ?? string.Empty, StringComparison.Ordinal);

        AppManagementGateway again = await harness.CreateGatewayAsync(
            new CountingAgent(succeed: true), approval: approval, installed: Harness.InstalledFeature());
        KazoPackageInstallReport reinstalled = await again.InstallPackageAsync(AppPackageTests.FeatureId, KazoPackageScope.System);

        Assert.Equal([ClientId], reinstalled.ReactivatedDependents);
        string after = await harness.Settings.ReadAsync("/etc/kazoos/apps.config") ?? string.Empty;
        Assert.Empty(AppsConfigDocument.ReadDisabledByDependency(after, AppPackageTests.FeatureId));
        Assert.DoesNotContain(ClientId + " = true", after, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientId + "=true", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updating_a_dependency_out_of_the_range_of_an_installed_app_is_refused()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", NeedsFeature), Harness.FeatureItem("1.0.0"));
        await (await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved)))
            .InstallPackageAsync(ClientId);
        harness.Catalog = Harness.CatalogWith(Harness.FeaturePackage("1.0.0"), Harness.ClientPackage("1.0.0", NeedsFeature));
        harness.Now += TimeSpan.FromMinutes(10);
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", NeedsFeature), Harness.FeatureItem("2.0.0", "1.0.0"));
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(
            agent, approval: new FakeApproval(AppInstallApprovalOutcome.Approved), installed: Harness.InstalledFeature());

        KazoPackageException refused = await Assert.ThrowsAsync<KazoPackageException>(
            () => gateway.InstallPackageAsync(AppPackageTests.FeatureId + "@2.0.0", KazoPackageScope.System).AsTask());

        // ADR 0248 §4: the dependents and their range are named; no --force, no administrator asked.
        Assert.Equal(AppPackageInstaller.DependencyConflictCode, refused.Code);
        Assert.Contains("Client (" + ClientId + ")", refused.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0 - 1.9.9", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task A_write_that_fails_in_the_middle_of_the_plan_undoes_the_packages_already_written()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishPlanCatalog(Harness.ClientItem("1.0.0", NeedsFeature), Harness.FeatureItem("1.0.0"));
        // Something of root's sits where the app's folder goes: its write fails after the feature's.
        await harness.Settings.WriteAsRootAsync("/home/alice/.local/share/kazoos/apps/" + ClientId, "in the way");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        await Assert.ThrowsAnyAsync<Exception>(() => gateway.InstallPackageAsync(ClientId).AsTask());

        // ADR 0248 §1 (ADR 0214 §3 extended to the plan): the feature written first is undone.
        InstalledAppsReadResult installed = await harness.Installer.ReadInstalledAsync(harness.Settings.CreateRootGateway());
        Assert.False(installed.Sources.ContainsKey(AppPackageTests.FeatureId));
    }

    private sealed partial class Harness
    {
        /// <summary>One entry of the official catalog of the tests of ADR 0248.</summary>
        internal sealed record CatalogItem(string AppId, string Kind, string Name, IReadOnlyList<(string Version, byte[] Package)> Versions, string? DependenciesJson);

        /// <summary>The client app: a window app of fixture with the given <c>dependencies</c>.</summary>
        internal static CatalogItem ClientItem(string version, string dependenciesJson) =>
            new(ClientId, "app", "Client", [(version, ClientPackage(version, dependenciesJson))], dependenciesJson);

        /// <summary>The feature of fixture (ADR 0260), its versions newest first.</summary>
        internal static CatalogItem FeatureItem(string latest, params string[] older) =>
            new(AppPackageTests.FeatureId, "feature", "Feature", [.. older.Prepend(latest).Select(version => (version, FeaturePackage(version)))], "[]");

        internal static byte[] ClientPackage(string version, string dependenciesJson)
        {
            Dictionary<string, byte[]> files = AppPackageTests.SampleFiles(version);
            string manifest = Encoding.UTF8.GetString(files["manifest.json"])
                .Replace("\"id\":\"" + AppPackageTests.AppId + "\"", "\"id\":\"" + ClientId + "\"", StringComparison.Ordinal)
                .Replace("\"name\":\"Sample\"", "\"name\":\"Client\"", StringComparison.Ordinal)
                .Replace("\"dependencies\":[]", "\"dependencies\":" + dependenciesJson, StringComparison.Ordinal);
            files["manifest.json"] = Encoding.UTF8.GetBytes(manifest);
            files["lib/Kazo.Sample.dll"] = File.ReadAllBytes(typeof(AppManifest).Assembly.Location);
            return AppPackageTests.Build(files, ClientId, version);
        }

        internal static byte[] FeaturePackage(string version)
        {
            Dictionary<string, byte[]> files = AppPackageTests.FeatureFiles(version);
            files["lib/Kazo.Feature.dll"] = File.ReadAllBytes(typeof(AppManifest).Assembly.Location);
            return AppPackageTests.Build(files, AppPackageTests.FeatureId, version, manifests: AppPackageTests.FeatureManifestPaths);
        }

        /// <summary>The running catalog once the OS loaded these packages: every manifest they carry, plus the terminal.</summary>
        internal static AppCatalog CatalogWith(params byte[][] packages)
        {
            List<AppManifest> apps = [Window("org.test.term", "Term")];
            // The packages target the harness's installer (SDK 2.1.0); the running catalog checks this build's SDK.
            AppSdkCompatibilityManifest sdk = new($"{KazoOsSdk.SemanticVersion.Major}.0.0");
            apps.AddRange(packages.SelectMany(static bytes => AppPackage.Read(bytes).Manifests).Select(manifest => manifest with { SdkCompatibility = sdk }));
            AppCatalogBuildResult built = AppCatalog.Build(apps, ["org.test.term"]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
            Assert.Empty(built.Catalog!.Rejected.Select(static rejection => rejection.Message));
            return built.Catalog!;
        }

        /// <summary>What the OS loaded at boot: the feature, installed from a package (so not an app of the OS).</summary>
        internal static InstalledPackageCatalog InstalledFeature()
        {
            // The feature and its entry points, as the OS loads them from the package.
            AppPackageSource source = AppPackageSource.ForCatalog(AppSourcesConfig.OfficialCatalogName, AppPackageTests.FeatureId, SemanticVersion.Parse("1.0.0"));
            InstalledPackageCatalog installed = new();
            installed.Apply([.. AppPackage.Read(FeaturePackage("1.0.0")).Manifests.Select(manifest => new InstalledApp(source, manifest))]);
            return installed;
        }

        /// <summary>Publishes the official catalog of this build's channel with these entries, each version signed.</summary>
        internal void PublishPlanCatalog(params CatalogItem[] items)
        {
            string address = AppPackageDefaults.OfficialCatalogAddressFor(KazoOsSdk.SemanticVersion);
            string root = address[..(address.LastIndexOf('/') + 1)];
            List<string> apps = [];
            foreach (CatalogItem item in items)
            {
                List<string> versions = [];
                foreach ((string version, byte[] package) in item.Versions)
                {
                    string folder = $"packages/{item.AppId}/{version}";
                    _downloader.Files[new Uri($"{root}{folder}/{item.AppId}-{version}.kazoapp")] = package;
                    versions.Add($$$"""
                        {"version":"{{{version}}}","commit":"abc","package":"{{{folder}}}/{{{item.AppId}}}-{{{version}}}.kazoapp",
                         "sha256":"{{{AppPackage.Sha256(package)}}}","size":{{{package.Length}}},{{{Signature(package, true)}}}
                         "capabilities":[],"sdkCompatibility":{"minimumVersion":"2.0.0"},"dependencies":{{{item.DependenciesJson ?? "[]"}}}}
                        """);
                }

                apps.Add($$$"""
                    {"appId":"{{{item.AppId}}}","kind":"{{{item.Kind}}}","name":"{{{item.Name}}}","category":"utilities","tags":[],
                     "shortDescriptions":{"en":"{{{item.Name}}}"},"license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-{{{item.Name}}}",
                     "latestVersion":"{{{item.Versions[0].Version}}}","versions":[{{{string.Join(',', versions)}}}]}
                    """);
            }

            _downloader.Files[new Uri(address)] = Encoding.UTF8.GetBytes($$$"""
                {"schemaVersion":1,"categories":[{"id":"utilities","labels":{"en":"Utilities"}}],"apps":[{{{string.Join(',', apps)}}}]}
                """);
        }
    }
}
