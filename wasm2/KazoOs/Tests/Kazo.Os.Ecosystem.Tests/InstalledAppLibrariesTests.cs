using System.Reflection;
using System.Reflection.Emit;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Kazo.Os.Platform.Core.Apps.Packages;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// The libraries of installed apps are loaded before the app, and one copy of a shared library
/// serves the page (ADR 0244).
/// </summary>
public sealed class InstalledAppLibrariesTests
{
    private const string Contracts = "Kazo.Sample.Contracts";
    private const string Protocol = "Kazo.Sample.Protocol";

    private static AppManifest Parse(string json) =>
        AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(json));

    private const string Tail =
        "\"sdkCompatibility\":{\"minimumVersion\":\"3.0.0\"},\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"hidden\",\"iconAssetPaths\":[]},"
        + "\"localizations\":[],\"capabilities\":[],\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,"
        + "\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,"
        + "\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],\"assets\":[],\"fileHandlers\":[],\"singleInstancePerUser\":false,"
        + "\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}";

    private static AppManifest Window(string id, string assembly = "Kazo.Sample.dll") => Parse(
        "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"name\":\"Sample\",\"version\":\"1.0.0\",\"publisherId\":\"pub.kazo\",\"description\":\"A sample.\","
        + "\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],\"assembly\":\"" + assembly + "\","
        + "\"type\":\"Kazo.Sample.SampleWindow\"}]}," + Tail);

    private static AppManifest FeatureManifest(string id) => Parse(
        "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"name\":\"Feature\",\"version\":\"1.0.0\",\"publisherId\":\"pub.kazo\",\"description\":\"A feature.\","
        + "\"kind\":\"feature\"," + Tail);

    private static AppManifest Child(string featureId) => Parse(
        "{\"schemaVersion\":1,\"id\":\"" + featureId + ".daemon\",\"name\":\"daemon\",\"version\":\"1.0.0\",\"publisherId\":\"pub.kazo\",\"description\":\"An entry point.\","
        + "\"kind\":\"service\",\"parent\":\"" + featureId + "\",\"entryPoint\":{\"assembly\":\"Kazo.Feature.dll\",\"type\":\"Kazo.Feature.Daemon\"}," + Tail);

    private static InstalledApp App(string id, params byte[][] libraries) =>
        new(new AppPackageSource("Kazo-ca", "Sample", SemanticVersion.Parse("1.0.0")), Window(id)) { Libraries = Describe(libraries) };

    private static InstalledApp Feature(string id, params byte[][] libraries) =>
        new(new AppPackageSource("Kazo-ca", "Feature", SemanticVersion.Parse("1.0.0")), FeatureManifest(id))
        {
            Children = [Child(id)],
            Libraries = Describe(libraries),
        };

    private static AppPackageLibrary[] Describe(byte[][] libraries) =>
        [.. libraries.Select(static bytes => AppPackageLibrary.Describe("library.dll", bytes))];

    /// <summary>A small real assembly, as <c>KazoOsPack</c> would put it in <c>lib/</c>.</summary>
    private static byte[] Build(string name, string version, bool shared = false)
    {
        PersistedAssemblyBuilder builder = new(new AssemblyName(name) { Version = Version.Parse(version) }, typeof(object).Assembly);
        if (shared)
        {
            ConstructorInfo metadata = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
            builder.SetCustomAttribute(new CustomAttributeBuilder(metadata, [AppPackageLibrary.SharedMetadataKey, "true"]));
        }

        builder.DefineDynamicModule(name).DefineType($"{name}.Marker", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        using MemoryStream stream = new();
        builder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The installed apps of a page, each library file readable from its owner's folder.</summary>
    private static InstalledAppsBoot Boot(params (InstalledApp App, byte[][] Files)[] apps)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach ((InstalledApp app, byte[][] libraries) in apps)
        {
            foreach (byte[] bytes in libraries)
            {
                files[app.GetAssemblyPath(AppPackageLibrary.Describe("library.dll", bytes).FileName).Value] = bytes;
            }
        }

        InstalledAppsBoot boot = new(BuildKnownLazyApps.Host);
        boot.Apply(
            new InstalledAppsImage([.. apps.Select(static item => item.App)], []),
            (path, _) => ValueTask.FromResult(files.GetValueOrDefault(path.Value)));
        Assert.Empty(boot.Warnings);
        return boot;
    }

    /// <summary>The page's assemblies, recorded instead of loaded: a test process keeps what it loads.</summary>
    private sealed class FakeRuntime : IInstalledLibraryRuntime
    {
        public Dictionary<string, Version> Loaded { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Loads { get; } = [];

        public Version? FindLoaded(string simpleName) => Loaded.GetValueOrDefault(simpleName);

        public void Load(byte[] assembly)
        {
            AppPackageLibrary library = AppPackageLibrary.Describe("library.dll", assembly);
            Loaded.Add(library.Name, library.Version);
            Loads.Add($"{library.Name} {InstalledLibraries.Format(library.Version)}");
        }
    }

    private sealed class RecordingLogger : ILogger<InstalledAppLibraries>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public async Task The_libraries_of_an_app_are_loaded_from_its_folder_before_its_own_assembly()
    {
        // Issue #419: Kazo.Sample.Protocol.dll is in lib/ and nothing else names it.
        byte[] protocol = Build(Protocol, "1.0.0.0");
        InstalledApp app = App("org.kazo.sample", protocol);
        FakeRuntime runtime = new();
        InstalledAppLibraries libraries = new(Boot((app, [protocol])), runtime: runtime);

        await libraries.LoadForAsync(app, CancellationToken.None);
        await libraries.LoadForAsync(app, CancellationToken.None);

        Assert.Equal(["Kazo.Sample.Protocol 1.0.0"], runtime.Loads);
        Assert.Contains(Protocol, libraries.LoadedFromPackages);
        Assert.Empty(libraries.GetPendingRestarts());
    }

    [Fact]
    public async Task Of_two_minor_versions_of_a_shared_library_the_highest_is_loaded()
    {
        byte[] older = Build(Contracts, "1.1.0.0", shared: true);
        byte[] newer = Build(Contracts, "1.2.0.0", shared: true);
        InstalledApp first = App("org.kazo.first", older);
        InstalledApp second = App("org.kazo.second", newer);
        FakeRuntime runtime = new();
        RecordingLogger logger = new();
        InstalledAppLibraries libraries = new(Boot((first, [older]), (second, [newer])), logger, runtime);

        await libraries.LoadForAsync(first, CancellationToken.None);
        await libraries.LoadForAsync(second, CancellationToken.None);

        Assert.Equal(["Kazo.Sample.Contracts 1.2.0"], runtime.Loads);
        Assert.Equal(
            ["Information: libraries: Kazo.Sample.Contracts 1.2.0 loaded for org.kazo.first (newest compatible, from org.kazo.second)"],
            logger.Lines);
        Assert.Empty(libraries.GetPendingRestarts());
    }

    [Fact]
    public async Task The_copy_of_an_installed_feature_is_loaded_for_the_app()
    {
        byte[] own = Build(Contracts, "1.3.0.0", shared: true);
        byte[] provided = Build(Contracts, "1.2.0.0", shared: true);
        InstalledApp app = App("org.kazo.client", own);
        InstalledApp feature = Feature("org.kazo.x86", provided);
        FakeRuntime runtime = new();
        RecordingLogger logger = new();
        InstalledAppLibraries libraries = new(Boot((app, [own]), (feature, [provided])), logger, runtime);

        await libraries.LoadForAsync(app, CancellationToken.None);

        Assert.Equal(["Kazo.Sample.Contracts 1.2.0"], runtime.Loads);
        Assert.Equal(["Information: libraries: Kazo.Sample.Contracts 1.2.0 loaded for org.kazo.client (feature org.kazo.x86)"], logger.Lines);
    }

    [Fact]
    public async Task A_copy_already_in_the_page_is_kept_and_the_app_waits_for_a_restart()
    {
        // ADR 0244 §3: .NET never unloads the copy; the OS says so once and offers a restart.
        byte[] older = Build(Contracts, "1.1.0.0", shared: true);
        byte[] newer = Build(Contracts, "1.2.0.0", shared: true);
        InstalledApp first = App("org.kazo.first", older);
        InstalledApp second = App("org.kazo.second", newer);
        FakeRuntime runtime = new();
        runtime.Loaded[Contracts] = new Version(1, 1, 0, 0);
        RecordingLogger logger = new();
        InstalledAppLibraries libraries = new(Boot((first, [older]), (second, [newer])), logger, runtime);

        await libraries.LoadForAsync(second, CancellationToken.None);
        await libraries.LoadForAsync(second, CancellationToken.None);
        libraries.ReportPendingRestarts();

        Assert.Empty(runtime.Loads);
        Assert.Equal(
            ["Warning: libraries: Kazo.Sample.Contracts 1.1.0 is already loaded and serves org.kazo.second; 1.2.0 (newest compatible, from org.kazo.second) applies at the next start of the OS"],
            logger.Lines);
        LibraryRestart restart = Assert.Single(libraries.GetPendingRestarts());
        Assert.Equal(("org.kazo.second", Contracts, new Version(1, 1, 0, 0), new Version(1, 2, 0, 0)), (restart.AppId, restart.Library, restart.Loaded, restart.Wanted));
    }

    [Fact]
    public async Task A_feature_installed_with_a_newer_copy_than_the_one_in_the_page_is_reported_after_the_install()
    {
        byte[] own = Build(Contracts, "1.1.0.0", shared: true);
        byte[] provided = Build(Contracts, "1.2.0.0", shared: true);
        InstalledApp app = App("org.kazo.client", own);
        InstalledApp feature = Feature("org.kazo.x86", provided);
        FakeRuntime runtime = new();
        RecordingLogger logger = new();
        InstalledAppsBoot boot = Boot((app, [own]));
        InstalledAppLibraries libraries = new(boot, logger, runtime);
        await libraries.LoadForAsync(app, CancellationToken.None);
        logger.Lines.Clear();

        // The feature arrives while 1.1 serves the page (ADR 0243 §6, ADR 0214 §1).
        boot.Refresh(new InstalledAppsImage([app, feature], []), static _ => false);
        IReadOnlyList<LibraryRestart> pending = libraries.ReportPendingRestarts();

        LibraryRestart restart = Assert.Single(pending);
        Assert.Equal("org.kazo.x86", restart.AppId);
        Assert.Equal(
            ["Warning: libraries: Kazo.Sample.Contracts 1.1.0 is already loaded and serves org.kazo.x86; 1.2.0 (feature org.kazo.x86) applies at the next start of the OS"],
            logger.Lines);
    }

    [Fact]
    public async Task A_library_that_is_not_shared_and_already_in_the_page_under_that_name_serves_the_second_app()
    {
        byte[] one = Build(Protocol, "1.0.0.0");
        byte[] two = Build(Protocol, "2.0.0.0");
        InstalledApp first = App("org.kazo.first", one);
        InstalledApp second = App("org.kazo.second", two);
        FakeRuntime runtime = new();
        RecordingLogger logger = new();
        InstalledAppLibraries libraries = new(Boot((first, [one]), (second, [two])), logger, runtime);

        await libraries.LoadForAsync(first, CancellationToken.None);
        await libraries.LoadForAsync(second, CancellationToken.None);

        Assert.Equal(["Kazo.Sample.Protocol 1.0.0"], runtime.Loads);
        Assert.Equal(
            ["Warning: libraries: Kazo.Sample.Protocol 2.0.0 of org.kazo.second not loaded, 1.0.0 is already in the page and serves it"],
            logger.Lines);
        // Not a shared contract: a restart would not choose between the two.
        Assert.Empty(libraries.GetPendingRestarts());
    }

    [Fact]
    public async Task A_listed_library_missing_from_the_folder_fails_the_launch_with_its_path()
    {
        byte[] protocol = Build(Protocol, "1.0.0.0");
        InstalledApp app = App("org.kazo.sample", protocol);
        InstalledAppLibraries libraries = new(Boot((app, [])), runtime: new FakeRuntime());

        FileNotFoundException missing = await Assert.ThrowsAsync<FileNotFoundException>(() => libraries.LoadForAsync(app, CancellationToken.None));

        Assert.Contains("/usr/lib/kazoos/apps/org.kazo.sample/1.0.0/lib/Kazo.Sample.Protocol.dll", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_load_context_tells_what_is_loaded_under_a_simple_name()
    {
        string name = "Kazo.Tests.LoadedLibrary" + Guid.NewGuid().ToString("N");
        Assert.Null(DefaultContextLibraryRuntime.Instance.FindLoaded(name));

        DefaultContextLibraryRuntime.Instance.Load(Build(name, "4.5.6.0"));

        Assert.Equal(new Version(4, 5, 6, 0), DefaultContextLibraryRuntime.Instance.FindLoaded(name));
        Assert.Equal(new Version(4, 5, 6, 0), DefaultContextLibraryRuntime.Instance.FindLoaded(name.ToUpperInvariant()));
    }

    private sealed class NoBuiltInTransport : IBuildKnownAssemblyTransport
    {
        public Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Assembly>>([]);
    }

    [Fact]
    public async Task The_transport_loads_the_libraries_before_the_assembly_of_the_app()
    {
        string appAssembly = "Kazo.Tests.LibraryOrder" + Guid.NewGuid().ToString("N");
        byte[] protocol = Build(Protocol, "1.0.0.0");
        InstalledApp app = new(new AppPackageSource("Kazo-ca", "Sample", SemanticVersion.Parse("1.0.0")), Window("org.kazo.sample", appAssembly + ".dll"))
        {
            Libraries = Describe([protocol]),
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [app.GetAssemblyPath("Kazo.Sample.Protocol.dll").Value] = protocol,
            [app.GetAssemblyPath(appAssembly + ".dll").Value] = Build(appAssembly, "1.0.0.0"),
        };
        InstalledAppsBoot boot = new(BuildKnownLazyApps.Host);
        List<string> reads = [];
        boot.Apply(new InstalledAppsImage([app], []), (path, _) =>
        {
            reads.Add(path.Value[(path.Value.LastIndexOf('/') + 1)..]);
            return ValueTask.FromResult(files.GetValueOrDefault(path.Value));
        });
        FakeRuntime runtime = new();
        InstalledAppAssemblyTransport transport = new(new NoBuiltInTransport(), boot) { Libraries = new InstalledAppLibraries(boot, runtime: runtime) };

        IReadOnlyList<Assembly> loaded = await transport.LoadAsync([appAssembly + ".dll"], CancellationToken.None);

        Assert.Equal(appAssembly, Assert.Single(loaded).GetName().Name);
        Assert.Equal(["Kazo.Sample.Protocol 1.0.0"], runtime.Loads);
        Assert.Equal(["Kazo.Sample.Protocol.dll", appAssembly + ".dll"], reads.Where(static name => name.EndsWith(".dll", StringComparison.Ordinal)));
    }
}
