using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>Why a service is not available (ADR 0262 §2 to §4).</summary>
public sealed partial class FeatureServiceResolverTests
{
    [Fact]
    public async Task A_disabled_feature_gives_null_and_raises_changed()
    {
        AppManifest client = FeatureServiceFixtures.Client();
        using Page page = new(FeatureServiceFixtures.Feature(), client);
        (object? before, _) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));
        List<string> changes = [];
        page.Resolver.Changed += (_, change) => changes.Add(change.ContractType);

        page.Enablement.MarkDisabled([FeatureServiceFixtures.FeatureId]);
        (object? after, KazoFeatureServiceStatus status) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        Assert.NotNull(before);
        Assert.Null(after);
        Assert.Equal(KazoFeatureServiceState.Disabled, status.State);
        Assert.Equal([typeof(IFixtureMachine).FullName!], changes);
    }

    [Fact]
    public void An_optional_dependency_out_of_range_keeps_the_app_in_the_catalog_and_gives_version_mismatch()
    {
        // ADR 0262 §3: absent, not removed from the catalog.
        AppManifest client = FeatureServiceFixtures.Client(minimum: "2.0.0");
        using Page page = new(FeatureServiceFixtures.Feature(), client);

        KazoFeatureServiceStatus status = page.Resolver.GetStatus(client, typeof(IFixtureMachine).FullName!);

        Assert.True(page.Catalog.Manifests.ContainsKey(FeatureServiceFixtures.ClientId));
        Assert.Equal(KazoFeatureServiceState.VersionMismatch, status.State);
        Assert.Equal("1.2.0", status.InstalledVersion);
        Assert.Equal("[2.0.0, )", status.RequestedRange);
    }

    [Fact]
    public void A_required_dependency_out_of_range_still_leaves_the_catalog()
    {
        AppCatalogBuildResult built = AppCatalog.Build([FeatureServiceFixtures.Feature(), FeatureServiceFixtures.Client(minimum: "2.0.0", optional: false)]);

        Assert.Contains(built.Errors, static error => error.Code == "catalog.dependency.incompatible");
    }

    [Fact]
    public async Task An_app_that_declares_no_dependency_on_the_feature_gets_not_declared()
    {
        AppManifest client = FeatureServiceFixtures.ClientWithoutDependency();
        using Page page = new(FeatureServiceFixtures.Feature(), client);

        (object? instance, KazoFeatureServiceStatus status) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        Assert.Null(instance);
        Assert.Equal(KazoFeatureServiceState.NotDeclared, status.State);
        Assert.Empty(page.Started);
    }

    [Fact]
    public async Task A_missing_permission_is_an_exception_of_the_operation_not_a_null()
    {
        AppManifest client = FeatureServiceFixtures.Client();
        using Page page = new(FeatureServiceFixtures.Feature(), client);

        (object? instance, _) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        IFixtureMachine machine = Assert.IsAssignableFrom<IFixtureMachine>(instance);
        using (KazoExecutionContext.Enter(new MinimalAppExecutionContext(client)))
        {
            UnauthorizedAccessException refused = Assert.Throws<UnauthorizedAccessException>(machine.Start);
            Assert.Contains(FeatureServiceFixtures.ClientId, refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_provider_whose_constructor_throws_gives_load_failed_then_is_tried_again()
    {
        FailingOnceService.Fail = true;
        AppManifest feature = FeatureServiceFixtures.Feature() with
        {
            ProvidedServices = [new(AppProvidedServiceManifest.NameOf(typeof(IFixtureMachine)), AppProvidedServiceManifest.NameOf(typeof(FailingOnceService)))],
        };
        AppManifest client = FeatureServiceFixtures.Client();
        using Page page = new(feature, client);

        (object? first, KazoFeatureServiceStatus failed) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));
        KazoFeatureServiceStatus reported = page.Resolver.GetStatus(client, typeof(IFixtureMachine).FullName!);
        (object? second, KazoFeatureServiceStatus available) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        Assert.Null(first);
        Assert.Equal(KazoFeatureServiceState.LoadFailed, failed.State);
        Assert.Equal("features.service.constructor-failed", failed.ErrorCode);
        Assert.Equal(KazoFeatureServiceState.LoadFailed, reported.State);
        Assert.Contains(page.FeatureLog.Entries, static entry => entry.Message.Contains("not ready", StringComparison.Ordinal));
        Assert.IsType<FailingOnceService>(second);
        Assert.Equal(KazoFeatureServiceState.Available, available.State);
    }

    [Fact]
    public async Task The_feature_itself_may_use_its_own_service_without_a_dependency()
    {
        AppManifest feature = FeatureServiceFixtures.Feature();
        using Page page = new(feature);

        (object? instance, _) = await page.Resolver.ResolveAsync(feature, typeof(IFixtureMachine));

        Assert.NotNull(instance);
    }
}
