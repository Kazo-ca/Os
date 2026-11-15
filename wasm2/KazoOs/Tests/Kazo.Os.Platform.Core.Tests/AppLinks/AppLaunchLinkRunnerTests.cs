using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.AppLinks;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.AppLinks;

/// <summary>ADR 0258 §3, §5, §6: installing and launching the app of a direct link for the guest.</summary>
public sealed class AppLaunchLinkRunnerTests
{
    private const string AppId = "ca.kazo.word";
    private const string OfficialSource = "catalog:official/ca.kazo.word";

    [Fact]
    public async Task Installs_from_the_official_catalog_for_the_user_then_launches()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Equal([(OfficialSource, KazoPackageScope.User)], fixture.Packages.Installs);
        Assert.Equal([AppId], fixture.Launched);
        Assert.Equal(["app-link.received", "app-link.installed", "app-link.launched"], fixture.Log.Messages);
        Assert.Empty(fixture.ActiveNotifications());
        Assert.Null(fixture.Runner.Current);
    }

    [Fact]
    public async Task Shows_the_catalog_name_publisher_and_version_while_installing()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];
        List<AppLaunchLinkOperation> shown = [];
        fixture.Runner.Changed += () =>
        {
            if (fixture.Runner.Current is { } operation)
            {
                shown.Add(operation);
            }
        };

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Equal(
            [AppLaunchLinkStep.Looking, AppLaunchLinkStep.Installing, AppLaunchLinkStep.Launching],
            shown.Select(static operation => operation.Step));
        AppLaunchLinkOperation installing = shown[1];
        Assert.Equal("Kazo Word", installing.Name);
        Assert.Equal("Kazo-ca", installing.Publisher);
        Assert.Equal("0.1.0", installing.Version);
        Assert.False(shown[2].CanCancel);
    }

    [Fact]
    public async Task An_app_already_installed_from_the_official_catalog_launches_without_reading_the_catalog()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Installed = [new KazoInstalledPackage(AppId, OfficialSource, "0.1.0", Loaded: true) { SystemWide = false }];
        fixture.Packages.CatalogsThrow = true;

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Empty(fixture.Packages.Installs);
        Assert.Equal([AppId], fixture.Launched);
        Assert.Equal(["app-link.received", "app-link.launched"], fixture.Log.Messages);
    }

    [Fact]
    public async Task The_same_app_installed_from_another_source_is_installed_again_from_the_official_catalog()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Installed = [new KazoInstalledPackage(AppId, "github:someone/word", "9.0.0", Loaded: true)];
        fixture.Packages.Catalogs = [Official(Word())];

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Equal([(OfficialSource, KazoPackageScope.User)], fixture.Packages.Installs);
    }

    [Fact]
    public async Task An_app_only_in_another_catalog_is_not_installed_and_the_store_is_offered()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(), new KazoAppCatalog("community", KazoPackageTrust.Trusted, [], [Word("community")])];

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Empty(fixture.Packages.Installs);
        Assert.Empty(fixture.Launched);
        Assert.Equal("not-offered", fixture.Log.LastReason);
        Notification notification = Assert.Single(fixture.ActiveNotifications());
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal("Le Kazo OS Store ne propose pas l'app ca.kazo.word.", notification.Message);
        NotificationAction action = Assert.Single(notification.Actions);
        Assert.Equal("Ouvrir le Kazo OS Store", action.Label);
        Assert.True(action.TryGetLaunchedApp(out string? store));
        Assert.Equal(AppLaunchLinkRunner.StoreAppId, store);
        Assert.Equal(["--app=ca.kazo.word"], action.LaunchArguments);
    }

    [Fact]
    public async Task A_theme_pack_with_the_same_id_is_not_an_app()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word() with { Kind = KazoCatalogItemKind.ThemePack })];

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Empty(fixture.Packages.Installs);
        Assert.Equal("not-offered", fixture.Log.LastReason);
    }

    [Fact]
    public async Task An_unreachable_official_catalog_says_so()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official() with { Error = "offline" }];

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Equal("unreachable", fixture.Log.LastReason);
        Assert.Equal(
            "Le Kazo OS Store est injoignable. Réessayez une fois en ligne.",
            Assert.Single(fixture.ActiveNotifications()).Message);
    }

    [Theory]
    [InlineData("apps.package.blocked", "Cette app est bloquée sur cet ordinateur.")]
    [InlineData("apps.package.download-failed", "Le Kazo OS Store est injoignable. Réessayez une fois en ligne.")]
    [InlineData("apps.package.bad-signature", "La signature du paquet a été refusée : il n'a pas été installé.")]
    [InlineData("apps.package.sdk-incompatible", "Cette version n'est pas compatible : needs OS 9.0")]
    [InlineData("apps.package.corrupt", "L'installation a échoué : needs OS 9.0")]
    public async Task A_refused_package_is_notified_with_the_usual_message(string code, string message)
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];
        fixture.Packages.InstallFailure = new KazoPackageException(code, "needs OS 9.0");

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Empty(fixture.Launched);
        Assert.Equal(code, fixture.Log.LastReason);
        Assert.Equal(message, Assert.Single(fixture.ActiveNotifications()).Message);
    }

    [Fact]
    public async Task A_refused_approval_is_notified()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];
        fixture.Packages.InstallFailure = new UnauthorizedAccessException("no");

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Equal("not-approved", fixture.Log.LastReason);
        Assert.Single(fixture.ActiveNotifications());
    }

    [Fact]
    public async Task Cancel_stops_the_install_without_a_notification()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];
        fixture.Packages.DuringInstall = fixture.Runner.Cancel;

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Empty(fixture.Launched);
        Assert.Equal("cancelled", fixture.Log.LastReason);
        Assert.Empty(fixture.ActiveNotifications());
        Assert.Null(fixture.Runner.Current);
    }

    [Fact]
    public async Task A_launch_that_fails_is_notified()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];
        fixture.LaunchStatus = AppLaunchStatus.EntryPointFault;

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Equal("launch:EntryPointFault", fixture.Log.LastReason);
        Assert.Single(fixture.ActiveNotifications());
    }

    [Fact]
    public async Task Without_a_session_nothing_is_installed()
    {
        // Arrange
        Fixture fixture = new() { User = null };

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Empty(fixture.Packages.Installs);
        Assert.Equal("no-session", fixture.Log.LastReason);
    }

    [Fact]
    public async Task A_host_that_cannot_install_apps_says_so()
    {
        // Arrange
        Fixture fixture = new() { HasGateway = false };

        // Act
        await fixture.Runner.RunAsync(Link());

        // Assert
        Assert.Equal("unsupported", fixture.Log.LastReason);
        Assert.Single(fixture.ActiveNotifications());
    }

    [Fact]
    public async Task An_unexpected_error_never_escapes()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];
        fixture.Packages.InstallFailure = new InvalidOperationException("boom");

        // Act
        Exception? escaped = await Record.ExceptionAsync(() => fixture.Runner.RunAsync(Link()));

        // Assert
        Assert.Null(escaped);
        Assert.Equal("error", fixture.Log.LastReason);
    }

    [Fact]
    public async Task A_second_link_while_one_runs_is_ignored()
    {
        // Arrange
        Fixture fixture = new();
        fixture.Packages.Catalogs = [Official(Word())];
        Task? second = null;
        fixture.Packages.DuringInstall = () => second = fixture.Runner.RunAsync(Link());

        // Act
        await fixture.Runner.RunAsync(Link());
        await second!;

        // Assert
        Assert.Single(fixture.Packages.Installs);
        Assert.Equal([AppId], fixture.Launched);
        Assert.Contains("busy", fixture.Log.Reasons);
    }

    [Fact]
    public void Opening_the_store_for_an_account_is_logged_without_installing_or_notifying()
    {
        // Arrange
        Fixture fixture = new();

        // Act
        fixture.Runner.RecordStoreOpened(Link());

        // Assert
        Assert.Equal(["app-link.store-opened"], fixture.Log.Messages);
        Assert.Empty(fixture.Packages.Installs);
        Assert.Empty(fixture.Launched);
        Assert.Empty(fixture.ActiveNotifications());
    }

    [Theory]
    [InlineData("https://github.com/Kazo-ca/Os-App-Word", "Kazo-ca")]
    [InlineData("https://github.com/", null)]
    [InlineData("not an address", null)]
    [InlineData(null, null)]
    public void The_publisher_is_the_owner_of_the_repository(string? repository, string? publisher) =>
        Assert.Equal(publisher, AppLaunchLinkRunner.PublisherOf(repository));

    private static AppLaunchLink Link()
    {
        Assert.True(AppLaunchLink.TryParse($"/?autolaunch={AppId}", out AppLaunchLink? link));
        return link!;
    }

    private static KazoCatalogApp Word(string catalog = "official") => new(
        catalog,
        AppId,
        "Kazo Word",
        "productivity",
        [],
        new Dictionary<string, string> { ["en"] = "Word processor" },
        "MIT",
        "https://github.com/Kazo-ca/Os-App-Word",
        "0.1.0",
        [new KazoCatalogAppVersion("0.1.0", 1024, [])],
        catalog == "official" ? KazoPackageTrust.Verified : KazoPackageTrust.Trusted);

    private static KazoAppCatalog Official(params KazoCatalogApp[] apps) =>
        new("official", KazoPackageTrust.Verified, [], apps);

    private static AuthenticatedPrincipal Guest()
    {
        LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse("guest"),
            "Invité",
            AppAuthority.User,
            group,
            [group],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch);
    }

    /// <summary>The runner over fakes, in French so the notifications read as the guest sees them.</summary>
    private sealed class Fixture
    {
        private readonly AuthenticatedPrincipal _guest = Guest();

        public Fixture()
        {
            User = _guest;
            Runner = new AppLaunchLinkRunner(
                () => User,
                _ => HasGateway ? Packages : null,
                (principal, appId) =>
                {
                    Assert.Same(_guest, principal);
                    if (LaunchStatus is AppLaunchStatus.Launched)
                    {
                        Launched.Add(appId);
                    }

                    return Task.FromResult(new AppLaunchResult(LaunchStatus));
                },
                Log,
                Notifications,
                static () => CultureInfo.GetCultureInfo("fr"),
                TimeProvider.System);
        }

        public AuthenticatedPrincipal? User { get; init; }

        public bool HasGateway { get; init; } = true;

        public AppLaunchStatus LaunchStatus { get; set; } = AppLaunchStatus.Launched;

        public FakePackages Packages { get; } = new();

        public RecordingLog Log { get; } = new();

        public InMemoryNotificationQueue Notifications { get; } = new(16);

        public List<string> Launched { get; } = [];

        public AppLaunchLinkRunner Runner { get; }

        public IReadOnlyList<Notification> ActiveNotifications() => Notifications.GetActive(_guest.UserId, DateTimeOffset.UtcNow);
    }

    /// <summary>The management gateway as the runner uses it: installed packages, catalogs, install.</summary>
    private sealed class FakePackages : IAppManagementGateway
    {
        public IReadOnlyList<KazoInstalledPackage> Installed { get; set; } = [];

        public IReadOnlyList<KazoAppCatalog> Catalogs { get; set; } = [];

        public bool CatalogsThrow { get; set; }

        public Exception? InstallFailure { get; set; }

        public Action? DuringInstall { get; set; }

        public List<(string Source, KazoPackageScope Scope)> Installs { get; } = [];

        public ValueTask<IReadOnlyList<KazoInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Installed);

        public ValueTask<IReadOnlyList<KazoAppCatalog>> GetPackageCatalogsAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
            CatalogsThrow
                ? throw new InvalidOperationException("The catalog must not be read.")
                : ValueTask.FromResult(Catalogs);

        public ValueTask<KazoPackageInstallReport> InstallPackageAsync(
            string source, KazoPackageScope scope = KazoPackageScope.Auto, CancellationToken cancellationToken = default)
        {
            DuringInstall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (InstallFailure is not null)
            {
                throw InstallFailure;
            }

            Installs.Add((source, scope));
            return ValueTask.FromResult(new KazoPackageInstallReport(AppId, "Kazo Word", "0.1.0", source, null, [], Trusted: true));
        }

        public ValueTask<IReadOnlyList<KazoAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> UninstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> ReinstallAsync(string appId, KazoInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>The lines of <c>/var/log/apps/org.kazoos.app-link.log</c>.</summary>
    private sealed class RecordingLog : IAppLoggingGateway
    {
        private readonly List<(string Message, IReadOnlyDictionary<string, string>? Properties)> _entries = [];

        public IReadOnlyList<string> Messages => [.. _entries.Select(static entry => entry.Message)];

        public IReadOnlyList<string> Reasons =>
            [.. _entries.Select(static entry => entry.Properties?.GetValueOrDefault("reason")).OfType<string>()];

        public string? LastReason => Reasons.LastOrDefault();

        public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null)
        {
            Assert.Equal(AppId, properties?["appId"]);
            _entries.Add((message, properties));
        }
    }
}
