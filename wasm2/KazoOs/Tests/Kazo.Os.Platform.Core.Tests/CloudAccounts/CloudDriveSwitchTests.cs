using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.CloudAccounts;

/// <summary>
/// ADR 0282 section 5 at run time: <c>KazoOs:CloudDrives:Disabled</c>, the <c>disabled</c> key of
/// <c>/etc/kazoos/cloud-drives.config</c>, and a switched-off type hidden from every place that
/// offers it (the provider list of Settings, the drive types of the Explorer and of <c>mount</c>).
/// </summary>
public sealed class CloudDriveSwitchTests
{
    private const string Account = "alice@example.test";

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("dropbox", new[] { "dropbox" })]
    [InlineData(" Dropbox, s3 ;OneDrive\tdropbox ", new[] { "dropbox", "s3", "onedrive" })]
    public void A_list_of_types_reads_as_an_administrator_writes_it(string? text, string[] expected)
    {
        Assert.Equal(expected, CloudProviderSettings.ParseTypes(text));
    }

    [Fact]
    public void A_disabled_provider_is_not_built_even_with_an_app_id()
    {
        CloudProviderSettings settings = new("full", "folder", "client") { Disabled = ["dropbox"] };

        IReadOnlyList<CloudOAuthProvider> providers = settings.BuildProviders();

        Assert.Equal(["onedrive"], providers.Select(static provider => provider.Id));
        Assert.True(settings.IsDisabled("dropbox"));
        Assert.False(settings.IsDisabled("onedrive"));
    }

    [Fact]
    public async Task The_system_file_adds_to_the_host_list_and_never_turns_a_type_back_on()
    {
        SettingsFilesHarness harness = new();
        await harness.WriteAsRootAsync("/etc/kazoos/cloud-drives.config", "disabled = OneDrive, s3\n");
        CloudProviderCatalog catalog = new(new CloudProviderSettings("full", "folder", "client") { Disabled = ["dropbox"] }, harness.Settings);

        Assert.Empty(await catalog.GetProvidersAsync());
        Assert.False(await catalog.IsTypeEnabledAsync("dropbox"));
        Assert.False(await catalog.IsTypeEnabledAsync("onedrive"));
        Assert.False(await catalog.IsTypeEnabledAsync("s3"));
        Assert.True(await catalog.IsTypeEnabledAsync("hostfs"));
    }

    [Fact]
    public async Task A_change_of_the_system_file_applies_without_restarting()
    {
        SettingsFilesHarness harness = new();
        CloudProviderCatalog catalog = new(new CloudProviderSettings("full", "folder"), harness.Settings);
        Assert.True(await catalog.IsTypeEnabledAsync("s3"));
        Assert.Single(await catalog.GetProvidersAsync());

        await harness.WriteAsRootAsync("/etc/kazoos/cloud-drives.config", "disabled = s3, dropbox\n");

        Assert.False(await catalog.IsTypeEnabledAsync("s3"));
        Assert.Empty(await catalog.GetProvidersAsync());
    }

    [Fact]
    public async Task A_switched_off_source_is_not_available_and_refuses_a_new_drive()
    {
        FakeRemoteDriveSource inner = new("s3", "S3");
        bool enabled = false;
        SwitchableDriveSource source = new(inner, _ => ValueTask.FromResult(enabled));

        Assert.Equal("s3", source.Type);
        Assert.Same(inner, source.Inner);
        Assert.False(await source.IsAvailableAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CreateAsync(new NetworkDriveRequest(Account)).AsTask());

        enabled = true;
        Assert.True(await source.IsAvailableAsync());
        inner.Available = false;
        Assert.False(await source.IsAvailableAsync());
    }

    [Fact]
    public void The_switch_forwards_the_availability_event_of_the_connector()
    {
        FakeRemoteDriveSource inner = new();
        SwitchableDriveSource source = new(inner, _ => ValueTask.FromResult(true));
        int raised = 0;
        source.AvailabilityChanged += () => raised++;

        inner.Online = false;
        inner.Online = true;

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_switched_off_type_is_neither_offered_to_apps_nor_mounted()
    {
        var fixture = await CreateSeededAsync();
        AuthenticatedPrincipal principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
        FakeRemoteDriveSource s3 = new("s3", "S3");
        s3.Account(Account).Put("x", "x.txt");
        bool enabled = false;
        NetworkDriveService drives = new(
            fixture.Router,
            [new HostFolderDriveSource(new FakeHostFolderAccess()), new SwitchableDriveSource(s3, _ => ValueTask.FromResult(enabled))],
            () => principal);
        AppDriveGateway gateway = new(() => fixture.Router, VirtualHardwareProfile.Default) { NetworkDrives = () => drives };

        Assert.DoesNotContain("s3", await gateway.GetNetworkDriveTypesAsync());
        NetworkDriveException refused = await Assert.ThrowsAsync<NetworkDriveException>(
            () => drives.MountAsync("s3", null, new NetworkDriveRequest(Account)).AsTask());
        Assert.Equal(NetworkDriveError.NotSupported, refused.Error);

        enabled = true;
        Assert.Contains("s3", await gateway.GetNetworkDriveTypesAsync());
        Assert.NotNull(await drives.MountAsync("s3", null, new NetworkDriveRequest(Account)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_drive_of_a_disabled_type_stays_in_the_list_unmounted_and_keeps_its_mount_point(bool connectorLeftOut)
    {
        var fixture = await CreateSeededAsync();
        AuthenticatedPrincipal principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
        FakeRemoteDriveSource s3 = new("s3", "S3");
        s3.Account(Account).Put("x", "x.txt");
        FakeRemoteDriveSource cloud = new();
        cloud.Account(Account).Put("y", "y.txt");
        NetworkDriveService before = new(fixture.Router, [s3, cloud], () => principal);
        await before.MountAsync("s3", null, new NetworkDriveRequest(Account));
        await before.UnmountAllAsync();

        // Next login: the administrator turned S3 off (or the publish left it out).
        INetworkDriveSource[] sources = connectorLeftOut
            ? [cloud]
            : [new SwitchableDriveSource(s3, _ => ValueTask.FromResult(false)), cloud];
        NetworkDriveService after = new(fixture.Router, sources, () => principal);
        await after.RestoreAsync();

        Assert.Empty(after.GetDrives());
        NetworkDriveException taken = await Assert.ThrowsAsync<NetworkDriveException>(
            () => after.MountAsync("fakecloud", "/mnt/S3", new NetworkDriveRequest(Account)).AsTask());
        Assert.Equal(NetworkDriveError.InvalidMountPoint, taken.Error);

        // Saving the list for another drive keeps the line of the disabled one.
        await after.MountAsync("fakecloud", null, new NetworkDriveRequest(Account));
        string table = await fixture.ReadTextAsync("/home/alice/" + NetworkDriveService.TableRelativePath, fixture.SystemContext());
        Assert.Contains("\"s3\"", table);
        Assert.Contains("/mnt/S3", table);

        // Turned back on, the drive returns at the next login.
        await after.UnmountAllAsync();
        NetworkDriveService again = new(fixture.Router, [s3, cloud], () => principal);
        await again.RestoreAsync();
        Assert.Equal(
            [("/mnt/FakeCloud", "fakecloud"), ("/mnt/S3", "s3")],
            again.GetDrives().Select(static drive => (drive.MountPoint.Value, drive.Type)).OrderBy(static d => d.Item1));
    }

    [Fact]
    public async Task A_drive_of_an_unknown_type_that_no_build_ever_shipped_is_still_dropped()
    {
        var fixture = await CreateSeededAsync();
        AuthenticatedPrincipal principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
        FakeRemoteDriveSource gone = new("gone", "Gone");
        gone.Account(Account).Put("x", "x.txt");
        FakeRemoteDriveSource cloud = new();
        cloud.Account(Account).Put("y", "y.txt");
        NetworkDriveService before = new(fixture.Router, [gone, cloud], () => principal);
        await before.MountAsync("gone", null, new NetworkDriveRequest(Account));
        await before.UnmountAllAsync();

        NetworkDriveService after = new(fixture.Router, [cloud], () => principal);
        await after.RestoreAsync();
        Assert.NotNull(await after.MountAsync("fakecloud", "/mnt/Gone", new NetworkDriveRequest(Account)));

        string table = await fixture.ReadTextAsync("/home/alice/" + NetworkDriveService.TableRelativePath, fixture.SystemContext());
        Assert.DoesNotContain("\"gone\"", table);
    }
}
