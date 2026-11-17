namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>
/// The <see cref="AppKind.SettingsPage"/> entry point of a feature (ADR 0249 §2): a page of
/// Settings ▸ Features, reserved to the children of a feature, with its order and its mobile switch.
/// </summary>
public sealed class FeatureSettingsPageManifestTests
{
    /// <summary>The page component of the fixture (a marker type: the validator never loads it).</summary>
    public sealed class AdminPage;

    /// <summary>A feature with one settings page that requires its role, and one command.</summary>
    public sealed class X86Pages(SettingsPageOptions? options = null, AppExecutionHost? execution = null, string[]? capabilities = null) : FeatureDefinition
    {
        public override string Id => "ca.kazo.x86";
        public override string Name => "Os-x86";
        public override string? CapabilityPrefix => "x86";
        public override IReadOnlyList<string> Capabilities => [AppCapabilities.DialogFileOpen];
        public override IReadOnlyList<FeatureRole> Roles => [new("x86:vm-admin", "VM administrator")];
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
        [
            new("admin", "Virtual machines", AppKind.SettingsPage, typeof(AdminPage))
            {
                RequiresRole = "x86:vm-admin",
                SettingsPage = options,
                Execution = execution,
                Capabilities = capabilities ?? [],
            },
        ];
    }

    private static AppManifest Page(X86Pages feature) =>
        Assert.Single(AppDefinitionMapper.ToFeatureManifests(feature, "3.2.0").Children);

    private static string[] Codes(AppManifest manifest) =>
        [.. AppManifestValidator.Validate(manifest).Errors.Select(static error => error.Code)];

    [Fact]
    public void A_settings_page_of_a_feature_is_valid_and_keeps_its_order_and_mobile_switch_through_json()
    {
        AppManifest page = Page(new X86Pages(new SettingsPageOptions { Order = 10, MobileSupported = false }));

        string json = AppManifestJsonSerializer.SerializeCanonical(page);
        AppManifest read = AppManifestJsonSerializer.DeserializeStrict(json);

        Assert.Empty(Codes(page));
        Assert.Contains("\"settingsPage\"", json, StringComparison.Ordinal);
        Assert.Equal(AppKind.SettingsPage, read.Kind);
        Assert.Equal("ca.kazo.x86", read.Parent);
        Assert.Equal("x86:vm-admin", read.RequiresRole);
        Assert.Equal(10, read.SettingsPage!.Order);
        Assert.False(read.SettingsPage.MobileSupported);
        Assert.Equal(AppExecutionHost.MainThread, read.Execution);
    }

    [Fact]
    public void A_settings_page_without_options_shows_on_mobile_in_first_position()
    {
        AppManifest page = Page(new X86Pages());

        SettingsPageOptions options = SettingsPageOptions.Of(page);

        Assert.Null(page.SettingsPage);
        Assert.Equal(0, options.Order);
        Assert.True(options.MobileSupported);
    }

    [Fact]
    public void An_ordinary_app_cannot_be_a_settings_page()
    {
        AppManifest ordinary = Page(new X86Pages()) with { Id = "org.test.pages", Parent = null, RequiresRole = null };

        Assert.Contains("manifest.feature.only", Codes(ordinary));
    }

    [Fact]
    public void Only_a_settings_page_declares_settings_page_options()
    {
        AppManifest window = Page(new X86Pages(new SettingsPageOptions { Order = 1 })) with { Kind = AppKind.Window };

        Assert.Contains("manifest.settingsPage.forbidden", Codes(window));
    }

    [Fact]
    public void A_settings_page_runs_on_the_main_thread()
    {
        AppManifest page = Page(new X86Pages(execution: AppExecutionHost.Pool));

        Assert.Contains("manifest.execution.window", Codes(page));
    }

    [Fact]
    public void A_settings_page_may_open_the_os_dialogs()
    {
        AppManifest page = Page(new X86Pages(capabilities: [AppCapabilities.DialogFileOpen]));

        Assert.DoesNotContain("manifest.capability.incompatible", Codes(page));
    }
}
