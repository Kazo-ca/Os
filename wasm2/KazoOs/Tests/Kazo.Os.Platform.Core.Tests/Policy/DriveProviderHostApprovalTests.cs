using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Policy;

namespace Kazo.Os.Platform.Core.Tests.Policy;

/// <summary>
/// ADR 0284 §1: <c>drives.provide</c> approves the hosts of the feature's drive providers, names them
/// and the typed servers on the approval screen, and asks again when an update adds a host; the
/// approved list keeps the hosts of both capabilities apart.
/// </summary>
public sealed class DriveProviderHostApprovalTests
{
    private const string FeatureId = "org.example.webdav";

    /// <summary>A feature with one provider that declares hosts and a server address field.</summary>
    private sealed class WebDavFeature(string[] hosts, string[]? directHosts = null) : FeatureDefinition
    {
        public override string Id => FeatureId;
        public override string Name => "WebDAV drives";
        public override string PublisherId => "org.example";
        public override IReadOnlyList<string> Capabilities =>
            directHosts is null ? [AppCapabilities.DrivesProvide] : [AppCapabilities.DrivesProvide, AppCapabilities.NetworkDirectAccess];
        public override IReadOnlyList<string> DirectNetworkHosts => directHosts ?? [];
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];
        public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
        [
            new("webdav", "WebDAV", typeof(Provider), AppDriveConnectionManifest.WithForm(
                new AppDriveFormFieldManifest("server", "Server address", AppDriveFormFieldKind.Url) { ServerAddress = true },
                new AppDriveFormFieldManifest("password", "Password", AppDriveFormFieldKind.Text) { Secret = true }))
            {
                Hosts = hosts,
            },
        ];
    }

    /// <summary>Stands for the provider's class.</summary>
    private sealed class Provider;

    private static AppManifest Feature(string[] hosts, string[]? directHosts = null) =>
        AppDefinitionMapper.ToFeatureManifests(new WebDavFeature(hosts, directHosts), KazoOsSdk.Version.ToString()).Feature;

    private static AppPermissionDecisions Allowed(string hosts) => AppPermissionDecisions.Parse(
        $"[permissions]\n{FeatureId}:drives.provide=allow\n[direct-network-hosts]\n{FeatureId}={hosts}\n", null);

    private static DangerousCapabilityState StateOf(AppManifest manifest, AppPermissionDecisions decisions) =>
        DangerousCapabilityEvaluator.Evaluate(manifest, false, DangerousCapabilityPolicy.Prompt, decisions)
            .Single(static status => status.Capability == AppCapabilities.DrivesProvide).State;

    [Fact]
    public void Drives_provide_names_the_provider_hosts_and_the_typed_server()
    {
        AppManifest feature = Feature(["api.example.com", "*.files.example.com"]);

        Assert.Equal("*.files.example.com, api.example.com", DirectNetworkHostText.For(feature, AppCapabilities.DrivesProvide));
        Assert.Equal("Server address", DirectNetworkHostText.TypedServersFor(feature, AppCapabilities.DrivesProvide));
        Assert.Null(DirectNetworkHostText.TypedServersFor(feature, AppCapabilities.NetworkDirectAccess));
        Assert.Null(DirectNetworkHostText.For(Feature([]), AppCapabilities.DrivesProvide));
    }

    [Fact]
    public void An_update_that_adds_a_provider_host_asks_again()
    {
        AppManifest updated = Feature(["api.example.com", "upload.example.com"]);

        Assert.Equal(DangerousCapabilityState.Allowed, StateOf(Feature(["api.example.com"]), Allowed("api.example.com")));
        Assert.Equal(DangerousCapabilityState.Pending, StateOf(updated, Allowed("api.example.com")));
        Assert.Equal(
            ["upload.example.com"],
            DangerousCapabilityEvaluator.UnapprovedDirectNetworkHosts(updated, Allowed("api.example.com"), AppCapabilities.DrivesProvide));
    }

    [Fact]
    public void Approving_or_refusing_one_capability_keeps_the_hosts_the_other_had_approved()
    {
        AppManifest feature = Feature(["api.example.com"], directHosts: ["stats.example.net"]);
        HashSet<string> before = new(StringComparer.Ordinal) { "stats.example.net", "gone.example.net" };

        IReadOnlyCollection<string>? afterAllow = DangerousCapabilityEvaluator.HostsToApprove(feature, before, [AppCapabilities.DrivesProvide], []);
        IReadOnlyCollection<string>? afterRefusal = DangerousCapabilityEvaluator.HostsToApprove(
            feature, new HashSet<string>(afterAllow!, StringComparer.Ordinal), [], [AppCapabilities.NetworkDirectAccess]);
        IReadOnlyCollection<string>? afterBoth = DangerousCapabilityEvaluator.HostsToApprove(
            feature, new HashSet<string>(afterRefusal!, StringComparer.Ordinal), [], [AppCapabilities.DrivesProvide]);

        Assert.Equal(["api.example.com", "stats.example.net"], afterAllow);
        Assert.Equal(["api.example.com"], afterRefusal);
        Assert.Null(afterBoth);
    }

    [Fact]
    public void Installing_the_feature_approves_every_host_at_once()
    {
        AppManifest feature = Feature(["api.example.com"], directHosts: ["stats.example.net"]);

        IReadOnlyCollection<string>? hosts = DangerousCapabilityEvaluator.HostsToApprove(
            feature, new HashSet<string>(), [AppCapabilities.DrivesProvide, AppCapabilities.NetworkDirectAccess], []);

        Assert.Equal(["api.example.com", "stats.example.net"], hosts);
    }
}
