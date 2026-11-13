using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

public sealed class StoreManifestTests
{
    [Fact]
    public void Manifest_is_the_store_window_on_desktop_and_mobile()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new StoreApp());

        Assert.Equal("org.kazoos.store", manifest.Id);
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.Equal("Kazo OS Store", manifest.Name);
        Assert.True(manifest.SingleInstancePerUser);
        Assert.Contains("desktop", manifest.Platform!.Supported);
        Assert.Contains("mobile", manifest.Platform!.Supported);
    }

    [Fact]
    public void Manifest_asks_only_to_manage_and_launch_apps_and_copy_a_link()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new StoreApp());

        Assert.Equal([AppCapabilities.AppsLaunch, AppCapabilities.AppsManage, AppCapabilities.ClipboardWrite], manifest.Capabilities.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_window_opens_large_enough_for_the_home_page()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new StoreApp());

        Assert.Equal(1100, manifest.Window!.DefaultWidth);
        Assert.Equal(720, manifest.Window.DefaultHeight);
        Assert.True(manifest.Window.Resizable);
    }

    [Fact]
    public void Manifest_passes_validation()
    {
        ManifestValidationResult result = AppManifestValidator.Validate(AppDefinitionMapper.ToManifest(new StoreApp()));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }
}
