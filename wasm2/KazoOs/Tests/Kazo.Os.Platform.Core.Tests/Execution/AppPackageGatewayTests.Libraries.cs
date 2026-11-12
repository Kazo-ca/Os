using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Tests.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// Installing a package that carries libraries of its own (ADR 0244): what is written, what is
/// refused, and what waits for a restart.
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    private const string Protocol = "Kazo.Sample.Protocol";
    private const string Contracts = "Kazo.Sample.Contracts";

    [Fact]
    public async Task A_package_with_a_library_is_installed_with_it()
    {
        // Issue #419: before ADR 0244 the app was refused for « assembly Kazo.Sample.Protocol ».
        Harness harness = await Harness.CreateAsync(absentAssemblies: [Protocol]);
        harness.PublishWithLibraries(Trusted, "1.0.0", TestLibraries.Build(Protocol, "1.0.0"));
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        KazoPackageInstallReport report = await gateway.InstallPackageAsync(Trusted);

        Assert.Equal("1.0.0", report.Version);
        Assert.NotNull(await harness.Settings.StatAsync($"{PersonalFolder}/1.0.0/lib/Kazo.Sample.Protocol.dll"));
        string description = (await harness.Settings.ReadAsync($"{PersonalFolder}/1.0.0/kazoos-package.json"))!;
        Assert.Contains("\"libraries\"", description, StringComparison.Ordinal);
        Assert.Contains(Protocol, description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_with_the_name_of_an_assembly_of_the_os_is_refused_before_any_approval()
    {
        // The runtime of the harness has every assembly: the library takes the name of one of them.
        Harness harness = await Harness.CreateAsync();
        harness.PublishWithLibraries(Untrusted, "1.0.0", TestLibraries.Build("System.Text.Json", "9.0.0"));
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);

        KazoPackageException refused = await Assert.ThrowsAsync<KazoPackageException>(
            async () => await (await harness.CreateGatewayAsync(agent, approval: approval)).InstallPackageAsync(Untrusted));

        Assert.Equal("apps.package.library-reserved", refused.Code);
        Assert.Equal(0, agent.Calls);
        Assert.Empty(approval.Requests);
        Assert.Null(await harness.Settings.StatAsync(Folder));
    }

    [Fact]
    public async Task A_shared_library_of_another_major_version_than_an_installed_features_copy_is_refused()
    {
        Harness harness = await Harness.CreateAsync(absentAssemblies: [Contracts]);
        harness.PublishWithLibraries(Trusted, "1.0.0", TestLibraries.Build(Contracts, "2.0.0", shared: true));
        InstalledPackageCatalog installed = new();
        installed.Apply([InstalledLibrariesTests.Feature(
            "org.kazo.x86", new AppPackageLibrary(Contracts, new Version(1, 4, 0, 0), new string('0', 64), Shared: true))]);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), installed: installed);

        KazoPackageException refused = await Assert.ThrowsAsync<KazoPackageException>(async () => await gateway.InstallPackageAsync(Trusted));

        Assert.Equal("apps.package.library-major-mismatch", refused.Code);
        Assert.Contains("org.kazo.x86", refused.Message, StringComparison.Ordinal);
        Assert.Null(await harness.Settings.StatAsync(PersonalFolder));
    }

    [Fact]
    public async Task Two_major_versions_of_a_shared_library_are_both_installed_without_a_feature()
    {
        Harness harness = await Harness.CreateAsync(absentAssemblies: [Contracts]);
        harness.PublishWithLibraries(Trusted, "1.0.0", TestLibraries.Build(Contracts, "2.0.0", shared: true));
        InstalledPackageCatalog installed = new();
        installed.Apply([InstalledLibrariesTests.App(
            "org.kazo.other", new AppPackageLibrary(Contracts, new Version(1, 4, 0, 0), new string('0', 64), Shared: true))]);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), installed: installed);

        KazoPackageInstallReport report = await gateway.InstallPackageAsync(Trusted);

        Assert.Equal("1.0.0", report.Version);
    }

    [Fact]
    public async Task A_library_already_in_the_page_from_another_package_is_not_taken_for_an_assembly_of_the_os()
    {
        // The runtime has Kazo.Sample.Contracts because another installed app loaded its copy.
        Harness harness = await Harness.CreateAsync();
        harness.PublishWithLibraries(Trusted, "1.0.0", TestLibraries.Build(Contracts, "1.0.0", shared: true));
        FixedLibraryState state = new([]) { LoadedFromPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Contracts } };
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), libraries: state);

        KazoPackageInstallReport report = await gateway.InstallPackageAsync(Trusted);

        Assert.Equal("1.0.0", report.Version);
    }

    [Fact]
    public async Task An_app_whose_shared_library_waits_for_a_restart_is_reported_as_restart_to_finish()
    {
        // ADR 0244 §3: the running catalog has the app at the installed version, yet an older copy
        // of its shared library is in the page; « Restart to finish » is offered as for an update.
        Harness harness = await Harness.CreateAsync(builtInSample: true, absentAssemblies: [Contracts]);
        harness.PublishWithLibraries(Trusted, "1.0.0", TestLibraries.Build(Contracts, "1.2.0", shared: true));
        InstalledPackageCatalog installed = new();
        installed.Apply([InstalledLibrariesTests.App(AppPackageTests.AppId)]);
        CountingAgent agent = new(succeed: true);

        AppManagementGateway served = await harness.CreateGatewayAsync(agent, installed: installed, libraries: new FixedLibraryState([]));
        KazoPackageInstallReport report = await served.InstallPackageAsync(Trusted);
        Assert.False(report.RestartRequired);
        Assert.True(Assert.Single(await served.GetInstalledPackagesAsync()).Loaded);

        FixedLibraryState waiting = new([new LibraryRestart(
            AppPackageTests.AppId, Contracts, new Version(1, 1, 0, 0), new Version(1, 2, 0, 0), "newest compatible, from org.kazo.sample")]);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, installed: installed, libraries: waiting);

        Assert.False(Assert.Single(await gateway.GetInstalledPackagesAsync()).Loaded);
    }

    /// <summary>What the page loaded of the installed apps' libraries, fixed by the test.</summary>
    private sealed class FixedLibraryState(IReadOnlyList<LibraryRestart> pending) : IInstalledLibraryState
    {
        public IReadOnlySet<string> LoadedFromPackages { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<LibraryRestart> GetPendingRestarts() => pending;
    }
}
