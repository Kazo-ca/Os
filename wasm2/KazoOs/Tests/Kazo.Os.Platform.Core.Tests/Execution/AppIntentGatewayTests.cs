using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// End-to-end tests for <see cref="IAppExecutionContext.Intents"/> exercising the real
/// <c>AppIntentGateway</c> through <see cref="AppExecutionContextFactory"/> and the real
/// <see cref="AppIntentDispatcher"/> -- the exact path a window app (e.g. File Explorer) uses
/// when it asks the system to open or launch something, rather than a mocked gateway.
/// </summary>
public sealed class AppIntentGatewayTests
{
    [Fact]
    public async Task OpenFileAsync_ResolvesSoleCandidate_AndLaunchesIt()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult result = await caller.Intents.OpenFileAsync(
            VirtualPath.Parse("/home/alice/notes.txt"));

        Assert.Equal(AppIntentOpenFileOutcome.Opened, result.Outcome);
    }

    [Fact]
    public async Task OpenFileAsync_NoHandler_ProposesTheStoreAppsThatOpenTheFile()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        fixture.Suggestions = await CreateSuggestionsAsync(fixture, refresh: true);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult opened = await caller.Intents.OpenFileAsync(VirtualPath.Parse("/home/alice/report.docx"));
        AppIntentOpenFileResult edited = await caller.Intents.EditFileAsync(VirtualPath.Parse("/home/alice/report.docx"));

        Assert.Equal(AppIntentOpenFileOutcome.NoHandler, opened.Outcome);
        Assert.Equal([KazoWordSuggestion], opened.SuggestedCatalogApps);
        Assert.Equal(AppIntentOpenFileOutcome.NoHandler, edited.Outcome);
        Assert.Equal(opened.SuggestedCatalogApps, edited.SuggestedCatalogApps);
    }

    [Fact]
    public async Task OpenFileAsync_NoHandler_ProposesNothingWhenNoCatalogWasDownloaded()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        fixture.Suggestions = await CreateSuggestionsAsync(fixture, refresh: false);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult result = await caller.Intents.OpenFileAsync(VirtualPath.Parse("/home/alice/report.docx"));

        Assert.Equal(AppIntentOpenFileOutcome.NoHandler, result.Outcome);
        Assert.Empty(result.SuggestedCatalogApps);
    }

    [Fact]
    public async Task SuggestCatalogAppsAsync_ListsTheStoreAppsForTheOpenWithBox()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        fixture.Suggestions = await CreateSuggestionsAsync(fixture, refresh: true);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        IReadOnlyList<AppIntentSuggestedCatalogApp> forDocx = await caller.Intents.SuggestCatalogAppsAsync(VirtualPath.Parse("/home/alice/report.docx"));
        IReadOnlyList<AppIntentSuggestedCatalogApp> forText = await caller.Intents.SuggestCatalogAppsAsync(VirtualPath.Parse("/home/alice/notes.txt"));

        AppIntentSuggestedCatalogApp word = Assert.Single(forDocx);
        Assert.Equal(KazoWordSuggestion, word);
        Assert.Equal("Un traitement de texte", word.ShortDescriptions["fr"]);
        Assert.Equal("https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/ca.kazo.word/0.1.0/icon.png", word.IconUri);
        Assert.Empty(forText);
    }

    [Fact]
    public async Task SuggestCatalogAppsAsync_IsDeniedWithoutAppsLaunch()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Suggestions = await CreateSuggestionsAsync(fixture, refresh: true);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            async () => await caller.Intents.SuggestCatalogAppsAsync(VirtualPath.Parse("/home/alice/report.docx")));
    }

    /// <summary>What the official catalog of <see cref="CatalogDownloader"/> proposes for a <c>.docx</c>.</summary>
    private static readonly AppIntentSuggestedCatalogApp KazoWordSuggestion = new("ca.kazo.word", "Kazo Word", "official")
    {
        ShortDescriptions = new Dictionary<string, string> { ["en"] = "A word processor", ["fr"] = "Un traitement de texte" },
        IconUri = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/ca.kazo.word/0.1.0/icon.png",
    };

    /// <summary>The official catalog lists Kazo Word, which declares <c>.docx</c>; downloaded only when <paramref name="refresh"/>.</summary>
    private static async Task<CatalogAppSuggestions> CreateSuggestionsAsync(Fixture fixture, bool refresh)
    {
        AppPackageInstaller installer = new(new CatalogDownloader(), sdkVersion: KazoOsSdk.SemanticVersion);
        if (refresh)
        {
            await installer.RefreshCatalogsAsync(AppSourcesConfigDocument.Read(null, AppPackageDefaults.Standard));
        }

        return new CatalogAppSuggestions(installer, fixture.CreateSystemReader, fixture.Catalog);
    }

    /// <summary>Serves an official <c>catalog.json</c> whose only app opens <c>.docx</c>.</summary>
    private sealed class CatalogDownloader : IAppPackageDownloader
    {
        public ValueTask<byte[]?> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<byte[]?>(uri.AbsoluteUri == AppPackageDefaults.OfficialCatalogAddress
                ? System.Text.Encoding.UTF8.GetBytes($$$"""
                    {"schemaVersion":1,"apps":[{"appId":"ca.kazo.word","name":"Kazo Word","category":"productivity","latestVersion":"0.1.0",
                     "shortDescriptions":{"en":"A word processor","fr":"Un traitement de texte"},
                     "versions":[{"version":"0.1.0","package":"packages/ca.kazo.word/0.1.0/ca.kazo.word-0.1.0.kazoapp","sha256":"{{{new string('a', 64)}}}",
                      "size":10,"capabilities":[],"sdkCompatibility":{"minimumVersion":"{{{KazoOsSdk.Version}}}"},
                      "icon":"packages/ca.kazo.word/0.1.0/icon.png",
                      "fileHandlers":[{"extensions":[".docx"],"actions":["open","edit"]}]}]}]}
                    """)
                : null);
    }

    [Fact]
    public async Task OpenFileAsync_MultipleCandidates_ReturnsChooserRequiredWithCandidates()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult result = await caller.Intents.OpenFileAsync(
            VirtualPath.Parse("/home/alice/notes.txt"));

        Assert.Equal(AppIntentOpenFileOutcome.ChooserRequired, result.Outcome);
        Assert.Equal(2, result.CandidateAppIds!.Count);
    }

    [Fact]
    public async Task OpenFileAsync_NoHandler_ReturnsNoHandler()
    {
        Fixture fixture = new(FileManagerManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult result = await caller.Intents.OpenFileAsync(
            VirtualPath.Parse("/home/alice/photo.png"));

        Assert.Equal(AppIntentOpenFileOutcome.NoHandler, result.Outcome);
    }

    [Fact]
    public async Task LaunchAsync_WithFileArgument_OpensThatFileInTheExplicitlyChosenApp()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentLaunchResult result = await caller.Intents.LaunchAsync(
            "org.kazoos.texteditor", ["/home/alice/notes.txt"]);

        Assert.Equal(AppIntentLaunchOutcome.Launched, result.Outcome);
    }

    [Fact]
    public async Task OpenFileAsync_WithoutAppsLaunchCapability_ThrowsAccessDenied()
    {
        AppManifest restrictedFileManager = FileManagerManifest() with { Capabilities = [] };
        Fixture fixture = new(restrictedFileManager, NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal, restrictedFileManager);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => caller.Intents.OpenFileAsync(VirtualPath.Parse("/home/alice/notes.txt")).AsTask());
    }

    private static AppManifest FileManagerManifest() => new()
    {
        Id = "org.kazoos.filemanager",
        Name = "File Manager",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "Exercises IAppExecutionContext.Intents as a calling app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.filemanager.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Capabilities = [AppCapabilities.AppsLaunch],
        Resources = AppResourceProfileManifest.None
    };

    private static AppManifest NotepadManifest() => new()
    {
        Id = "org.kazoos.notepad",
        Name = "Notepad",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "A simple text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.notepad.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private static AppManifest TextEditorManifest() => new()
    {
        Id = "org.kazoos.texteditor",
        Name = "Text Editor",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "An advanced text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.texteditor.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly LocalLoginName _aliceLoginName;
        private AppIntentDispatcher? _dispatcher;

        internal Fixture(params AppManifest[] manifests)
        {
            FixedTimeProvider timeProvider = new(_now);
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                timeProvider);
            FileSystemMountRouter router = new(repository);
            Grants = new CapabilityGrantRepository(() => _now);
            InMemoryTopicMessageBus topicBus = new(Grants);
            FileSystemService fileSystem = new(
                router, new FileSystemPathResolver(router), new FileSystemAuthorizer(), topicBus,
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            FileSystemSeeder seeder = new(fileSystem, timeProvider);

            InMemoryLocalUserRepository users = new(() => _now);
            InMemoryLocalGroupRepository groups = new();
            InMemoryEventBus eventBus = new();
            BoundedAuditLog auditLog = new(maxEntries: 100);

            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            _aliceLoginName = LocalLoginName.Parse("alice");
            users.CreateUser(
                _aliceLoginName, "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            ManualSimulationClock clock = new(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(clock, Session = new LocalSessionService(
                users, seeder, eventBus, auditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now), eventBus);
            InMemoryNotificationQueue notifications = new(maxEntriesPerUser: 20);
            BoundedDiagnosticSink diagnostics = new(maxEntries: 100);
            Settings = new OsSettingsService(fileSystem, Session, clock);

            // Resolved lazily, mirroring the production wiring in EcosystemServiceCollectionExtensions:
            // AppIntentDispatcher depends (via AppLifecycleOrchestrator) on this very factory.
            AppExecutionContextFactory contextFactory = new(
                Grants, fileSystem, eventBus, topicBus, notifications, diagnostics, clock, Manager,
                intentDispatcherProvider: () => _dispatcher!);

            AppCatalogBuildResult catalogResult = AppCatalog.Build(manifests);
            Assert.True(catalogResult.IsSuccess, string.Join(", ", catalogResult.Errors.Select(e => e.Message)));
            AppCatalog catalog = catalogResult.Catalog!;

            Dictionary<string, AppDescriptor> descriptors = new(StringComparer.Ordinal);
            foreach (AppManifest manifest in manifests)
            {
                descriptors[manifest.Id] = new AppDescriptor(manifest, typeof(object), typeof(object).Assembly);
            }

            AppEnablementRegistry enablement = new(catalog);
            Orchestrator = new AppLifecycleOrchestrator(
                catalog, descriptors, enablement, Manager, Grants, contextFactory);
            FileAssociationResolver resolver = new(catalog, enablement, Settings);
            _dispatcher = new AppIntentDispatcher(Orchestrator, catalog, enablement, resolver, Grants, fileSystem)
            {
                CatalogSuggestions = () => Suggestions,
            };
            FileSystem = fileSystem;
            Clock = clock;
            Catalog = catalog;
        }

        /// <summary>Gets or sets the Store search the dispatcher asks on <c>NoHandler</c> (ADR 0257 §2).</summary>
        internal CatalogAppSuggestions? Suggestions { get; set; }

        internal IFileSystemService FileSystem { get; }

        internal ISimulationClock Clock { get; }

        internal AppCatalog Catalog { get; }

        /// <summary>The OS's own reader of <c>/etc</c>, as the update check and the suggestions use it.</summary>
        internal IAppFileSystemGateway CreateSystemReader() => new AppFileSystemGateway(
            FileSystem,
            Clock,
            new AppOperationContext
            {
                AppId = "org.kazoos.shell",
                UserId = "system",
                UserName = "system",
                UserAuthority = AppAuthority.System,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = true
            },
            ["system"]);

        internal LocalSessionService Session { get; }
        internal InMemoryProcessManager Manager { get; }
        internal CapabilityGrantRepository Grants { get; }
        internal IOsSettingsService Settings { get; }
        internal AppLifecycleOrchestrator Orchestrator { get; }

        internal Task<AuthenticatedPrincipal> LoginAsync() => Session.LoginAsync(_aliceLoginName, "hunter2");

        internal void Grant(string appId, AuthenticatedPrincipal principal, string capability) =>
            Grants.Grant(appId, principal.UserId.ToString(), capability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);

        /// <summary>Launches the file-manager (caller) window app and returns its live execution context.</summary>
        internal async Task<IAppExecutionContext> LaunchCallerAsync(
            AuthenticatedPrincipal principal, AppManifest? callerManifest = null)
        {
            string appId = (callerManifest ?? FileManagerManifest()).Id;
            AppLaunchResult result = await Orchestrator.LaunchAsync(new AppLaunchRequest(appId, principal, []));
            Assert.Equal(AppLaunchStatus.Launched, result.Status);
            return result.Context!;
        }

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
