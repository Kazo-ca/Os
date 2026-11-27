using System.Collections.Immutable;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Commands.Kazopkg;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary><c>kazopkg</c> (ADR 0182 §4, ADR 0189 §6): arguments and the <see cref="KazoAppPackages"/> calls.</summary>
public sealed class KazopkgCommandTests
{
    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("install")]
    [InlineData("install", "a/b", "c/d")]
    [InlineData("install", "a/b", "--allow-untrusted")]
    [InlineData("install", "a/b", "--user", "--system")]
    [InlineData("list", "--user")]
    [InlineData("remove", "--force", "x")]
    [InlineData("sources", "add")]
    [InlineData("sources", "remove", "x", "--trusted")]
    [InlineData("policy", "block", "planet", "x")]
    [InlineData("policy", "block", "app")]
    public async Task Bad_arguments_print_the_usage_and_exit_2_without_calling_the_os(params string[] args)
    {
        FakePackages packages = new();

        (int exit, _, string stderr) = await RunAsync(packages, args);

        Assert.Equal(2, exit);
        Assert.Contains("usage: kazopkg install <appId|repo>[@version] [--user|--system]", stderr, StringComparison.Ordinal);
        Assert.Empty(packages.Calls);
    }

    [Theory]
    [InlineData(new string[0], "Auto")]
    [InlineData(new[] { "--user" }, "User")]
    [InlineData(new[] { "--system" }, "System")]
    public async Task Install_passes_the_scope_and_reports_the_version_the_capabilities_and_the_restart(string[] options, string scope)
    {
        FakePackages packages = new();

        (int exit, string stdout, string stderr) = await RunAsync(packages, ["install", "Kazo-ca/Os-App-Webamp", .. options]);

        Assert.Equal(0, exit);
        Assert.Equal([$"install Kazo-ca/Os-App-Webamp scope={scope}"], packages.Calls);
        Assert.Contains("Webamp 1.0.0 installed from github:Kazo-ca/Os-App-Webamp@1.0.0", stdout, StringComparison.Ordinal);
        Assert.Contains(scope == "System" ? "installed for every user" : "installed for you only", stdout, StringComparison.Ordinal);
        Assert.Contains("network.real.access (!)", stdout, StringComparison.Ordinal);
        Assert.Contains("restart Kazo OS to finish", stdout, StringComparison.Ordinal);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task A_refused_package_prints_its_reason_and_exits_1()
    {
        FakePackages packages = new() { Refusal = new KazoPackageException("apps.package.blocked", "org.kazo.webamp is blocked") };

        (int exit, _, string stderr) = await RunAsync(packages, "install", "someone/app", "--user");

        Assert.Equal(1, exit);
        Assert.Contains("kazopkg: org.kazo.webamp is blocked", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_shows_the_scope_and_marks_the_apps_waiting_for_a_restart()
    {
        (int exit, string stdout, _) = await RunAsync(new FakePackages(), "list");

        Assert.Equal(0, exit);
        Assert.Contains("org.kazo.webamp  1.0.0  system  github:Kazo-ca/Os-App-Webamp", stdout, StringComparison.Ordinal);
        Assert.Contains("org.kazo.irc     0.2.0  user  github:Kazo-ca/Os-App-IRC (restart pending)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_names_what_stops_asks_and_keeps_the_feature_installed()
    {
        FakePackages refused = new() { Apps = FeatureApps(KazoAppInstallState.Installed) };
        FakePackages confirmed = new() { Apps = FeatureApps(KazoAppInstallState.Installed) };

        (int no, string asked, string cancelled) = await RunWithInputAsync(refused, "n\n", "disable", "org.kazo.webamp");
        (int yes, string stdout, _) = await RunAsync(confirmed, "disable", "org.kazo.webamp", "--yes");

        // ADR 0287 §6: the dependents and the processes are named before anything stops.
        Assert.Equal(1, no);
        Assert.Contains("These apps depend on it and are disabled with it: VM client", asked, StringComparison.Ordinal);
        Assert.Contains("These processes will be stopped: vmclient (42)", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("Console", asked, StringComparison.Ordinal);
        Assert.Contains("Cancelled", cancelled, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(refused.Calls);
        Assert.Equal(0, yes);
        Assert.Equal(["disable org.kazo.webamp"], confirmed.Calls);
        Assert.Contains("org.kazo.webamp is disabled for every user. It stays installed", stdout, StringComparison.Ordinal);
        Assert.Contains("stopped: vmclient (42), forced after its grace period", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enable_puts_a_disabled_feature_back_and_list_and_info_mark_it_while_disabled()
    {
        FakePackages packages = new() { Apps = FeatureApps(KazoAppInstallState.Disabled) };

        (int listed, string list, _) = await RunAsync(packages, "list");
        (int enabled, string stdout, _) = await RunAsync(packages, "enable", "org.kazo.webamp");

        Assert.Equal(0, listed);
        Assert.Contains("org.kazo.webamp  1.0.0  system  github:Kazo-ca/Os-App-Webamp (disabled)", list, StringComparison.Ordinal);
        Assert.DoesNotContain("IRC (disabled)", list, StringComparison.Ordinal);
        Assert.Equal(0, enabled);
        Assert.Equal(["enable org.kazo.webamp"], packages.Calls);
        Assert.Contains("org.kazo.webamp is enabled again.", stdout, StringComparison.Ordinal);
    }

    /// <summary>The installed package as a feature, one of its entry points, and an app that needs it.</summary>
    private static IReadOnlyList<KazoAppInstallInfo> FeatureApps(KazoAppInstallState state) =>
    [
        new(new KazoAppInfo("org.kazo.webamp", "Webamp", "Webamp.", AppKind.Feature, "system", null, "1.0.0", "ca.kazo", false, true, []), state, []),
        new(new KazoAppInfo("org.kazo.webamp.cli", "Console", "Console.", AppKind.Terminal, "system", null, "1.0.0", "ca.kazo", false, true, []) { ParentId = "org.kazo.webamp" }, state, []),
        new(new KazoAppInfo("org.kazo.vmclient", "VM client", "VM client.", AppKind.Window, "system", null, "1.0.0", "ca.kazo", false, true, []), state, []),
    ];

    [Fact]
    public async Task Update_installs_only_newer_versions_where_the_copy_is()
    {
        FakePackages packages = new();

        (int exit, string stdout, _) = await RunAsync(packages, "update");

        Assert.Equal(0, exit);
        Assert.Contains("install github:Kazo-ca/Os-App-Webamp@1.1.0 scope=System", packages.Calls);
        Assert.DoesNotContain(packages.Calls, call => call.StartsWith("install github:Kazo-ca/Os-App-IRC", StringComparison.Ordinal));
        Assert.Contains("org.kazo.irc 0.2.0 is up to date", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_passes_the_scope()
    {
        FakePackages packages = new();

        (int exit, string stdout, _) = await RunAsync(packages, "remove", "org.kazo.irc", "--user");

        Assert.Equal(0, exit);
        Assert.Equal(["remove org.kazo.irc scope=User"], packages.Calls);
        Assert.Contains("org.kazo.irc 0.2.0 removed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_app_never_loaded_is_available_at_once_and_its_signature_is_shown()
    {
        (int exit, string stdout, _) = await RunAsync(new FakePackages { Immediate = true }, "install", "Kazo-ca/Os-App-Webamp");

        Assert.Equal(0, exit);
        Assert.Contains("kazopkg: signature verified", stdout, StringComparison.Ordinal);
        Assert.Contains("kazopkg: available now", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("restart Kazo OS", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_passes_the_scope_and_reports_both_versions()
    {
        FakePackages packages = new();

        (int exit, string stdout, _) = await RunAsync(packages, "rollback", "org.kazo.webamp", "--system");

        Assert.Equal(0, exit);
        Assert.Equal(["rollback org.kazo.webamp scope=System"], packages.Calls);
        Assert.Contains("kazopkg: Webamp went back from 1.1.0 to 1.0.0", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "repair system=False", "kazopkg: nothing to repair")]
    [InlineData(new[] { "--system" }, "repair system=True", "kazopkg: org.kazo.irc: install of 0.3.0 undone")]
    public async Task Repair_reports_what_it_did(string[] options, string call, string output)
    {
        FakePackages packages = new();

        (int exit, string stdout, _) = await RunAsync(packages, ["repair", .. options]);

        Assert.Equal(0, exit);
        Assert.Equal([call], packages.Calls);
        Assert.Contains(output, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_refuses_the_user_option()
    {
        (int exit, _, string stderr) = await RunAsync(new FakePackages(), "repair", "--user");

        Assert.Equal(2, exit);
        Assert.Contains("usage: kazopkg", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sources_lists_catalogs_trusted_and_untrusted_sources()
    {
        (int exit, string stdout, _) = await RunAsync(new FakePackages(), "sources");

        Assert.Equal(0, exit);
        Assert.Contains("catalog: official https://example.org/catalog.json (verified (built in))", stdout, StringComparison.Ordinal);
        Assert.Contains("trusted: catalog:official (built in), github.com/Kazo-ca (built in)", stdout, StringComparison.Ordinal);
        Assert.Contains("untrusted: github.com/someone", stdout, StringComparison.Ordinal);
        Assert.Contains("updates: every 6 h", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sources_add_and_remove_call_the_os()
    {
        FakePackages packages = new();

        (int added, string stdout, _) = await RunAsync(packages, "sources", "add", "github:someone", "--trusted");
        (int removed, _, _) = await RunAsync(packages, "sources", "remove", "github:someone");

        Assert.Equal((0, 0), (added, removed));
        Assert.Equal(["sources add github:someone trusted=True", "sources remove github:someone"], packages.Calls);
        Assert.Contains("source github:someone added and trusted", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_lists_and_edits_the_block_list()
    {
        FakePackages packages = new();

        (int listed, string list, _) = await RunAsync(packages, "policy");
        (int blocked, _, _) = await RunAsync(packages, "policy", "block", "category", "social");
        (int unblocked, _, _) = await RunAsync(packages, "policy", "unblock", "capability", "network.real.access");

        Assert.Equal((0, 0, 0), (listed, blocked, unblocked));
        Assert.Contains("blocked categories:   social", list, StringComparison.Ordinal);
        Assert.Equal(["policy Category social True", "policy Capability network.real.access False"], packages.Calls);
    }

    [Theory]
    [InlineData("n", false)]
    [InlineData("", false)]
    [InlineData("y", true)]
    [InlineData("yes", true)]
    public async Task Install_prints_the_plan_of_an_app_with_dependencies_and_asks_first(string answer, bool installed)
    {
        FakePackages packages = new() { Plan = FeaturePlan() };

        (int exit, string stdout, string stderr) = await RunWithInputAsync(packages, answer + "\n", "install", "org.kazo.client");

        // ADR 0248 §1: the plan, then one question; the answer decides before anything is written.
        Assert.Contains("this installation installs, in this order:", stdout, StringComparison.Ordinal);
        Assert.Contains("Feature 1.0.0 from catalog:official/org.kazo.feature@1.0.0", stdout, StringComparison.Ordinal);
        Assert.Contains("dangerous permissions: filesystem.system.read", stdout, StringComparison.Ordinal);
        Assert.Contains("an administrator approves this installation once", stdout, StringComparison.Ordinal);
        Assert.Contains("Continue? [y/N]", stdout, StringComparison.Ordinal);
        Assert.Equal(installed ? 0 : 1, exit);
        Assert.Equal(installed, packages.Calls.Contains("install org.kazo.client@1.0.0 scope=Auto"));
        Assert.Equal(!installed, stderr.Contains("cancelled, nothing was installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_with_yes_does_not_ask()
    {
        FakePackages packages = new() { Plan = FeaturePlan() };

        (int exit, string stdout, _) = await RunAsync(packages, "install", "org.kazo.client", "--yes");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Continue?", stdout, StringComparison.Ordinal);
        // The version of the plan the user saw is the one installed.
        Assert.Contains("install org.kazo.client@1.0.0 scope=Auto", packages.Calls);
    }

    [Fact]
    public async Task A_missing_dependency_stops_the_installation_with_its_reason()
    {
        FakePackages packages = new()
        {
            Refusal = new KazoPackageException(
                "apps.package.dependency-unresolved",
                "Client (org.kazo.client 1.0.0) needs org.kazo.feature >= 1.0.0, which none of the configured catalogs publishes."),
        };

        (int exit, _, string stderr) = await RunAsync(packages, "install", "org.kazo.client");

        Assert.Equal(1, exit);
        Assert.Contains("kazopkg: the installation of org.kazo.client is stopped: Client (org.kazo.client 1.0.0) needs org.kazo.feature >= 1.0.0", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remove_warns_about_the_apps_it_disables(bool yes)
    {
        FakePackages packages = new() { Dependents = [new KazoPackageDependent("org.kazo.client", "Client")] };

        (int exit, string stdout, string stderr) = yes
            ? await RunAsync(packages, "remove", "org.kazo.feature", "--yes")
            : await RunAsync(packages, "remove", "org.kazo.feature");

        // ADR 0248 §3: the dependents are named; without an answer nothing is removed.
        Assert.Contains("Client will no longer work and will be disabled", stdout, StringComparison.Ordinal);
        Assert.Equal(yes ? 0 : 1, exit);
        Assert.Equal(yes, packages.Calls.Contains("remove org.kazo.feature scope=Auto"));
        if (yes)
        {
            Assert.Contains("kazopkg: org.kazo.client disabled", stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("cancelled", stderr, StringComparison.Ordinal);
        }
    }

    private static KazoPackageInstallPlan FeaturePlan() => new(
        [
            new KazoPlannedPackage(
                "org.kazo.feature", "Feature", "1.0.0", "catalog:official/org.kazo.feature@1.0.0", null, 2048,
                SystemWide: true, IsFeature: true, KazoPackageTrust.Verified, ["filesystem.system.read"], Requested: false),
            new KazoPlannedPackage(
                "org.kazo.client", "Client", "1.0.0", "catalog:official/org.kazo.client@1.0.0", null, 1024,
                SystemWide: false, IsFeature: false, KazoPackageTrust.Verified, [], Requested: true),
        ],
        [],
        RequiresAdministrator: true);

    [Fact]
    public async Task A_host_without_packages_says_so()
    {
        (int exit, _, string stderr) = await RunAsync(new FakePackages { Supported = false }, "sources");

        Assert.Equal(1, exit);
        Assert.Contains("cannot install apps from a repository", stderr, StringComparison.Ordinal);
    }

    private static Task<(int Exit, string Stdout, string Stderr)> RunAsync(FakePackages packages, params string[] args) =>
        RunWithInputAsync(packages, input: null, args);

    private static async Task<(int Exit, string Stdout, string Stderr)> RunWithInputAsync(FakePackages packages, string? input, params string[] args)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new KazopkgApp());
        PackagesContext app = new(manifest, packages);
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app: app,
            arguments: args.ToImmutableArray(),
            standardInput: input is null ? TextReader.Null : new StringReader(input),
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/root",
            environment: ImmutableDictionary<string, string>.Empty);
        using IDisposable scope = KazoExecutionContext.Enter(app);
        int exit = await new KazopkgCommand(manifest).ExecuteAsync(context, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class FakePackages : IAppManagementGateway
    {
        public bool Supported { get; init; } = true;

        public KazoPackageException? Refusal { get; init; }

        public bool Immediate { get; init; }

        public List<string> Calls { get; } = [];

        /// <summary>The plan <c>kazopkg install</c> reads first (ADR 0248 §1); none says the OS predates it.</summary>
        public KazoPackageInstallPlan? Plan { get; init; }

        /// <summary>The apps that need the package <c>kazopkg remove</c> is asked to remove (ADR 0248 §3).</summary>
        public IReadOnlyList<KazoPackageDependent> Dependents { get; init; } = [];

        public ValueTask<KazoPackageInstallPlan> PlanPackageInstallAsync(
            string source, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default) =>
            Plan is { } plan ? ValueTask.FromResult(plan) : throw new NotSupportedException();

        public ValueTask<IReadOnlyList<KazoPackageDependent>> GetPackageDependentsAsync(string appId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Dependents);

        /// <summary>The catalog apps with their state (ADR 0287); none says the OS predates the list.</summary>
        public IReadOnlyList<KazoAppInstallInfo>? Apps { get; init; }

        public ValueTask<IReadOnlyList<KazoAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Apps is { } apps ? ValueTask.FromResult(apps) : throw new NotSupportedException();

        public ValueTask<KazoUninstallPlan> PlanUninstallAsync(string appId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KazoUninstallPlan(
                ["org.kazo.vmclient", appId + ".cli", appId], [new KazoRunningProcessInfo(42, "org.kazo.vmclient", "vmclient", "alice")]));

        public ValueTask<KazoDisableReport> DisableFeatureAsync(string featureId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"disable {featureId}");
            return ValueTask.FromResult(new KazoDisableReport(
                ["org.kazo.vmclient", featureId], [new KazoRunningProcessInfo(42, "org.kazo.vmclient", "vmclient", "alice", Forced: true)]));
        }

        public ValueTask<IReadOnlyList<string>> EnableFeatureAsync(string featureId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"enable {featureId}");
            return ValueTask.FromResult<IReadOnlyList<string>>([featureId, "org.kazo.vmclient"]);
        }

        public ValueTask<IReadOnlyList<string>> UninstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> ReinstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<KazoPackageSources> GetPackageSourcesAsync(CancellationToken cancellationToken = default) =>
            Supported ? ValueTask.FromResult(Sources()) : throw new NotSupportedException();

        public ValueTask<IReadOnlyList<KazoInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoInstalledPackage>>(
            [
                new("org.kazo.webamp", "github:Kazo-ca/Os-App-Webamp", "1.0.0", Loaded: true),
                new("org.kazo.irc", "github:Kazo-ca/Os-App-IRC", "0.2.0", Loaded: false) { SystemWide = false }
            ]);

        public ValueTask<KazoPackageInfo> GetPackageInfoAsync(string source, CancellationToken cancellationToken = default)
        {
            Calls.Add($"info {source}");
            bool webamp = source.Contains("Webamp", StringComparison.Ordinal);
            return ValueTask.FromResult(webamp
                ? new KazoPackageInfo(source, "org.kazo.webamp", ["1.1.0", "1.0.0"], "1.1.0", true, "1.0.0")
                : new KazoPackageInfo(source, "org.kazo.irc", ["0.2.0"], "0.2.0", true, "0.2.0"));
        }

        public ValueTask<KazoPackageInstallReport> InstallPackageAsync(
            string source, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default)
        {
            Calls.Add($"install {source} scope={scope}");
            if (Refusal is not null)
            {
                throw Refusal;
            }

            string version = source.EndsWith("@1.1.0", StringComparison.Ordinal) ? "1.1.0" : "1.0.0";
            return ValueTask.FromResult(new KazoPackageInstallReport(
                "org.kazo.webamp", "Webamp", version, $"github:Kazo-ca/Os-App-Webamp@{version}",
                version == "1.0.0" ? null : "1.0.0", [AppCapabilities.NetworkRealAccess], Trusted: true)
            {
                SystemWide = scope == KazoPackageScope.System,
                RestartRequired = !Immediate,
                Signed = Immediate,
            });
        }

        public ValueTask<KazoPackageInstallReport> RollbackPackageAsync(
            string appId, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default)
        {
            Calls.Add($"rollback {appId} scope={scope}");
            return ValueTask.FromResult(new KazoPackageInstallReport(
                appId, "Webamp", "1.0.0", "github:Kazo-ca/Os-App-Webamp@1.0.0", "1.1.0", [], Trusted: true) { RestartRequired = false });
        }

        public ValueTask<IReadOnlyList<string>> RepairPackagesAsync(bool system, CancellationToken cancellationToken = default)
        {
            Calls.Add($"repair system={system}");
            return ValueTask.FromResult<IReadOnlyList<string>>(system ? ["org.kazo.irc: install of 0.3.0 undone, 0.2.0 stays installed"] : []);
        }

        public ValueTask<KazoInstalledPackage> RemovePackageAsync(
            string appId, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default)
        {
            Calls.Add($"remove {appId} scope={scope}");
            return ValueTask.FromResult(new KazoInstalledPackage(appId, "github:Kazo-ca/Os-App-IRC", "0.2.0", Loaded: true)
            {
                DisabledDependents = [.. Dependents.Select(static app => app.AppId)],
            });
        }

        public ValueTask<KazoPackageSources> AddPackageSourceAsync(string source, bool trusted, CancellationToken cancellationToken = default)
        {
            Calls.Add($"sources add {source} trusted={trusted}");
            return ValueTask.FromResult(Sources());
        }

        public ValueTask<KazoPackageSources> RemovePackageSourceAsync(string source, CancellationToken cancellationToken = default)
        {
            Calls.Add($"sources remove {source}");
            return ValueTask.FromResult(Sources());
        }

        public ValueTask<KazoPackagePolicy> GetPackagePolicyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KazoPackagePolicy([], ["social"], [], []));

        public ValueTask<KazoPackagePolicy> SetPackagePolicyAsync(
            KazoPackageBlockKind kind, string value, bool blocked, CancellationToken cancellationToken = default)
        {
            Calls.Add($"policy {kind} {value} {blocked}");
            return ValueTask.FromResult(new KazoPackagePolicy([], [], [], []));
        }

        private static KazoPackageSources Sources() => new("raw.githubusercontent.com", ["catalog:official", "github.com/Kazo-ca"], [])
        {
            Catalogs = [new KazoPackageCatalog("official", "https://example.org/catalog.json", KazoPackageTrust.Verified, BuiltIn: true)],
            BuiltInSources = ["catalog:official", "github.com/Kazo-ca"],
            UntrustedSources = ["github.com/someone"],
            UpdateIntervalHours = 6,
        };
    }

    private sealed class PackagesContext(AppManifest manifest, IAppManagementGateway packages) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.Administrator;
        public IAppManagementGateway AppManagement => packages;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(Manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }
}
