using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Settings;

/// <summary>
/// Issue #407: an app that declares only <c>filesystem.private.*</c> creates its own
/// <c>~/.config/&lt;appId&gt;/</c> with <see cref="KazoDirectory.CreateDirectoryAsync(string, CancellationToken)"/>
/// on the real filesystem, although it may not look at <c>~/.config</c> itself.
/// </summary>
public sealed class PrivateConfigDirectoryTests
{
    private const string AppId = "ca.kazo.private-only";

    [Fact]
    public async Task An_app_with_only_private_capabilities_creates_its_config_directory_and_writes_in_it()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        IAppFileSystemGateway gateway = harness.CreateAppGateway(
            alice, AppId, AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite);
        string directory = $"{alice.HomePath}/.config/{AppId}";

        using (KazoExecutionContext.Enter(Context(gateway, alice)))
        {
            KazoDirectoryInfo created = await KazoDirectory.CreateDirectoryAsync(directory);
            await KazoDirectory.CreateDirectoryAsync(directory);
            await KazoFile.WriteAllTextAsync($"{directory}/state.json", "{}");

            Assert.True(created.Exists);
            Assert.Equal("{}", await KazoFile.ReadAllTextAsync($"{directory}/state.json"));
        }

        Assert.Equal("{}", await harness.ReadAsync($"{directory}/state.json"));
    }

    [Fact]
    public async Task The_app_still_cannot_create_a_folder_outside_its_private_directory()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        IAppFileSystemGateway gateway = harness.CreateAppGateway(
            alice, AppId, AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite);

        using (KazoExecutionContext.Enter(Context(gateway, alice)))
        {
            await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
                () => KazoDirectory.CreateDirectoryAsync($"{alice.HomePath}/.config/another.app"));
            await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
                () => KazoDirectory.CreateDirectoryAsync($"{alice.HomePath}/Documents/new"));
        }
    }

    private static MinimalAppExecutionContext Context(IAppFileSystemGateway gateway, AuthenticatedPrincipal principal) =>
        new(
            new AppManifest
            {
                Id = AppId,
                Name = "Private only",
                Version = "1.0.0",
                PublisherId = "ca.kazo",
                Description = "App with only filesystem.private.* (issue #407).",
                Kind = AppKind.Window,
                EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "PrivateOnly"),
                SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
                Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
                Resources = AppResourceProfileManifest.None
            },
            fileSystem: gateway,
            userId: principal.UserId.ToString(),
            homeDirectory: principal.HomePath);
}
