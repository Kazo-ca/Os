using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Policy;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>The checks a feature's service makes of its caller (ADR 0246 §3, §4).</summary>
public sealed partial class FeaturePermissionsTests
{
    private static ICapabilityCatalog WithFeature() =>
        new FeatureCapabilityCatalog(BuiltInCapabilityCatalog.Instance, [FeaturePermissionFixtures.Feature()]);

    [Fact]
    public void A_service_grants_the_operation_whose_permission_the_caller_holds_and_refuses_the_other()
    {
        // The case of Os-x86: x86.vm.use granted, x86.vm.destroy not approved.
        CapabilityGrantRepository grants = Grants();
        Grant(grants, FeaturePermissionFixtures.Use);
        KazoFeatureCaller caller = new(FeaturePermissionFixtures.ClientId, 42, User) { Permissions = Checker(grants, WithFeature()) };

        caller.Require(FeaturePermissionFixtures.Use);
        UnauthorizedAccessException refused = Assert.Throws<UnauthorizedAccessException>(() => caller.Require(FeaturePermissionFixtures.Destroy));

        Assert.True(caller.Has(FeaturePermissionFixtures.Use));
        Assert.False(caller.Has(FeaturePermissionFixtures.Destroy));
        Assert.Contains(FeaturePermissionFixtures.Destroy, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_covering_permission_grants_what_it_covers()
    {
        CapabilityGrantRepository grants = Grants();
        Grant(grants, FeaturePermissionFixtures.All);
        AppCapabilityChecker checker = Checker(grants, WithFeature());

        Assert.True(checker.Evaluate(FeaturePermissionFixtures.Use).Granted);
        Assert.True(checker.Evaluate(FeaturePermissionFixtures.Destroy).Granted);
        Assert.False(checker.Evaluate(AppCapabilities.ClipboardRead).Granted);
    }

    [Fact]
    public void A_permission_of_a_feature_that_left_is_refused_whatever_grant_remains()
    {
        // ADR 0246 §5: the lines and grants stay; they apply again once the feature is reinstalled.
        CapabilityGrantRepository grants = Grants();
        Grant(grants, FeaturePermissionFixtures.Use);

        Assert.False(Checker(grants, BuiltInCapabilityCatalog.Instance).Evaluate(FeaturePermissionFixtures.Use).Granted);
        Assert.True(Checker(grants, WithFeature()).Evaluate(FeaturePermissionFixtures.Use).Granted);
    }

    [Fact]
    public void A_caller_without_known_permissions_holds_none()
    {
        KazoFeatureCaller caller = new(FeaturePermissionFixtures.ClientId, 42, User);

        Assert.False(caller.Has(FeaturePermissionFixtures.Use));
        Assert.Throws<UnauthorizedAccessException>(() => caller.Require(FeaturePermissionFixtures.Use));
    }

    [Fact]
    public void A_machine_decision_about_a_features_dangerous_permission_or_a_topic_is_read()
    {
        const string topic = "topic-subscribe:app/org.test.other/events";
        string file = $"""
            [permissions]
            {FeaturePermissionFixtures.ClientId}:{FeaturePermissionFixtures.Destroy}=allow
            {FeaturePermissionFixtures.ClientId}:{topic}=deny
            {FeaturePermissionFixtures.ClientId}:{FeaturePermissionFixtures.Use}=allow
            """;

        AppPermissionDecisions withFeature = AppPermissionDecisions.Parse(file, null, "/etc/kazoos/app-permissions.config", "~/.config/kazoos/app-permissions.config", WithFeature());
        AppPermissionDecisions without = AppPermissionDecisions.Parse(file, null, "/etc/kazoos/app-permissions.config", "~/.config/kazoos/app-permissions.config", BuiltInCapabilityCatalog.Instance);

        Assert.Equal(AppPermissionDecision.Allow, withFeature.GetMachineDecision(FeaturePermissionFixtures.ClientId, FeaturePermissionFixtures.Destroy));
        Assert.Equal(AppPermissionDecision.Deny, withFeature.GetMachineDecision(FeaturePermissionFixtures.ClientId, topic));
        // A normal permission needs no decision: ignored without effect.
        Assert.Null(withFeature.GetMachineDecision(FeaturePermissionFixtures.ClientId, FeaturePermissionFixtures.Use));
        Assert.Null(without.GetMachineDecision(FeaturePermissionFixtures.ClientId, FeaturePermissionFixtures.Destroy));
        Assert.Contains(without.Warnings, static warning => warning.Contains("unknown capability", StringComparison.Ordinal));
    }

    [Fact]
    public void The_evaluator_lists_a_features_dangerous_permissions_with_the_features_texts()
    {
        AppManifest client = FeaturePermissionFixtures.Client();

        IReadOnlyList<DangerousCapabilityStatus> statuses = DangerousCapabilityEvaluator.Evaluate(
            client, false, DangerousCapabilityPolicy.Prompt, AppPermissionDecisions.Empty, WithFeature());

        DangerousCapabilityStatus destroy = Assert.Single(statuses);
        Assert.Equal(FeaturePermissionFixtures.Destroy, destroy.Capability);
        Assert.Equal(DangerousCapabilityState.Pending, destroy.State);
        Assert.Equal(FeaturePermissionFixtures.FeatureId, destroy.Description.ProviderId);
        Assert.True(DangerousCapabilityEvaluator.DeclaresDangerousCapabilities(client, WithFeature()));
        Assert.False(DangerousCapabilityEvaluator.DeclaresDangerousCapabilities(client));
    }
}
