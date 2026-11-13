using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>
/// The <c>fileHandlers</c> of <c>catalog.json</c> (ADR 0257 §1) and the search of the Store apps
/// that open a file no installed app opens (ADR 0257 §2).
/// </summary>
public sealed class CatalogAppSuggestionsTests
{
    private static readonly Uri Address = new(AppPackageDefaults.OfficialCatalogAddress);
    private static readonly SemanticVersion Sdk = SemanticVersion.Parse("3.3.0");
    private static readonly AppPolicy NoBlock = Policy();
    private static readonly IReadOnlySet<string> NothingInstalled = new HashSet<string>(StringComparer.Ordinal);

    private const string WordHandlers = """
        "fileHandlers":[{"mediaType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document","extensions":[".docx"],"actions":["open","edit"]}]
        """;

    private static string Version(string version, string handlers = WordHandlers, string sdk = "3.0.0", string capabilities = "\"storage.app\"") =>
        $$$"""{"version":"{{{version}}}","package":"packages/x/{{{version}}}/x-{{{version}}}.kazoapp","sha256":"{{{new string('a', 64)}}}","size":10,"capabilities":[{{{capabilities}}}],"sdkCompatibility":{"minimumVersion":"{{{sdk}}}"}{{{(handlers.Length > 0 ? "," + handlers : string.Empty)}}}}""";

    private static string App(string appId, string name, string latest, params string[] versions) =>
        $$$"""{"appId":"{{{appId}}}","name":"{{{name}}}","category":"productivity","latestVersion":"{{{latest}}}","versions":[{{{string.Join(',', versions)}}}]}""";

    private static byte[] CatalogBytes(params string[] apps) =>
        Encoding.UTF8.GetBytes($$$"""{"schemaVersion":1,"apps":[{{{string.Join(',', apps)}}}]}""");

    private static AppCatalogListing Parse(string name, params string[] apps) =>
        AppCatalogDocument.Parse(name, Address, CatalogBytes(apps));

    private static AppPolicy Policy(string[]? apps = null, string[]? categories = null, string[]? capabilities = null) => new(
        new HashSet<string>(apps ?? [], StringComparer.Ordinal),
        new HashSet<string>(categories ?? [], StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(capabilities ?? [], StringComparer.Ordinal),
        []);

    private static IReadOnlyList<AppIntentSuggestedCatalogApp> Select(
        IReadOnlyList<(string, AppCatalogListing)> catalogs,
        string? extension = ".docx",
        string? mediaType = null,
        string action = "open",
        IReadOnlySet<string>? installed = null,
        AppPolicy? policy = null) =>
        CatalogAppSuggestions.Select(catalogs, extension, mediaType, action, installed ?? NothingInstalled, policy ?? NoBlock, Sdk);

    [Fact]
    public void A_catalog_version_reads_its_file_handlers()
    {
        AppCatalogListing listing = Parse("official", App("ca.kazo.word", "Kazo Word", "0.1.0", Version("0.1.0")));

        FileHandlerManifest handler = Assert.Single(Assert.Single(listing.Apps).Latest.FileHandlers);
        Assert.Equal([".docx"], handler.Extensions);
        Assert.Equal(["open", "edit"], handler.Actions);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", handler.MediaType);
    }

    [Fact]
    public void A_catalog_without_file_handlers_stays_valid_and_declares_none()
    {
        AppCatalogListing listing = Parse("official", App("ca.kazo.word", "Kazo Word", "0.1.0", Version("0.1.0", handlers: string.Empty)));

        Assert.Empty(Assert.Single(listing.Apps).Latest.FileHandlers);
        Assert.Empty(listing.Warnings);
    }

    [Fact]
    public void Bad_file_handler_entries_are_skipped_and_extensions_are_normalized()
    {
        const string handlers = """
            "fileHandlers":[
              "not an object",
              {"extensions":[".md"],"actions":[]},
              {"extensions":["txt","../x",".TXT"," .Log "],"actions":["Open"]},
              {"mediaType":"text/csv","actions":["open"]}
            ]
            """;

        AppCatalogVersion version = Assert.Single(Parse("official", App("org.example.text", "Text", "1.0.0", Version("1.0.0", handlers))).Apps).Latest;

        Assert.Equal(2, version.FileHandlers.Count);
        Assert.Equal([".txt", ".log"], version.FileHandlers[0].Extensions);
        Assert.Equal(["open"], version.FileHandlers[0].Actions);
        Assert.True(version.HandlesFile(".TXT", null, "open"));
        Assert.True(version.HandlesFile(null, "TEXT/CSV", "open"));
        Assert.False(version.HandlesFile(".md", null, "open"));
        Assert.False(version.HandlesFile(".txt", null, "edit"));
    }

    [Fact]
    public void The_number_of_file_handlers_read_is_capped()
    {
        string many = "\"fileHandlers\":[" + string.Join(',', Enumerable.Range(0, AppCatalogDocument.MaximumFileHandlers + 10)
            .Select(i => $$"""{"extensions":[".e{{i}}"],"actions":["open"]}""")) + "]";

        AppCatalogVersion version = Assert.Single(Parse("official", App("org.example.many", "Many", "1.0.0", Version("1.0.0", many))).Apps).Latest;

        Assert.Equal(AppCatalogDocument.MaximumFileHandlers, version.FileHandlers.Count);
    }

    [Fact]
    public void An_app_that_declares_the_extension_is_proposed_with_its_catalog()
    {
        AppCatalogListing official = Parse("official",
            App("ca.kazo.word", "Kazo Word", "0.1.0", Version("0.1.0")),
            App("org.example.audio", "Audio", "1.0.0", Version("1.0.0", """ "fileHandlers":[{"extensions":[".mp3"],"actions":["open"]}] """)));

        AppIntentSuggestedCatalogApp app = Assert.Single(Select([("official", official)]));

        Assert.Equal(new AppIntentSuggestedCatalogApp("ca.kazo.word", "Kazo Word", "official"), app);
    }

    [Fact]
    public void Without_an_extension_the_media_type_is_used()
    {
        AppCatalogListing official = Parse("official", App("ca.kazo.word", "Kazo Word", "0.1.0", Version("0.1.0")));

        Assert.Single(Select([("official", official)], extension: null,
            mediaType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        Assert.Empty(Select([("official", official)], extension: ".odt"));
    }

    [Fact]
    public void The_action_must_be_declared()
    {
        AppCatalogListing official = Parse("official",
            App("org.example.viewer", "Viewer", "1.0.0", Version("1.0.0", """ "fileHandlers":[{"extensions":[".docx"],"actions":["open"]}] """)));

        Assert.Single(Select([("official", official)], action: "open"));
        Assert.Empty(Select([("official", official)], action: "edit"));
    }

    [Fact]
    public void Installed_and_blocked_apps_are_left_out()
    {
        AppCatalogListing official = Parse("official", App("ca.kazo.word", "Kazo Word", "0.1.0", Version("0.1.0", capabilities: "\"network.client\"")));
        IReadOnlyList<(string, AppCatalogListing)> catalogs = [("official", official)];

        Assert.Empty(Select(catalogs, installed: new HashSet<string>(["ca.kazo.word"], StringComparer.Ordinal)));
        Assert.Empty(Select(catalogs, policy: Policy(apps: ["ca.kazo.word"])));
        Assert.Empty(Select(catalogs, policy: Policy(categories: ["Productivity"])));
        Assert.Empty(Select(catalogs, policy: Policy(capabilities: ["network.client"])));
    }

    [Fact]
    public void The_newest_stable_version_compatible_with_the_sdk_decides()
    {
        AppCatalogListing official = Parse("official",
            // 2.0.0 needs SDK 4 and opens .docx; 1.0.0 runs on SDK 3 but declares nothing: not proposed.
            App("org.example.next", "Next", "2.0.0", Version("2.0.0", sdk: "4.0.0"), Version("1.0.0", handlers: string.Empty)),
            // 1.1.0-beta.1 opens .docx but is a pre-release; 1.0.0 is the stable one and opens it too.
            App("org.example.beta", "Beta", "1.0.0", Version("1.1.0-beta.1"), Version("1.0.0")),
            // Only 1.0.0 runs on SDK 3, and it opens .docx even though 2.0.0 no longer does.
            App("org.example.older", "Older", "2.0.0", Version("2.0.0", handlers: string.Empty, sdk: "4.0.0"), Version("1.0.0")),
            // The stable version opens nothing; only the pre-release does: not proposed.
            App("org.example.preview", "Preview", "1.0.0", Version("1.1.0-beta.1"), Version("1.0.0", handlers: string.Empty)));

        Assert.Equal(["org.example.beta", "org.example.older"], Select([("official", official)]).Select(app => app.AppId));
    }

    [Fact]
    public void The_official_catalog_comes_first_and_an_app_is_proposed_once()
    {
        AppCatalogListing official = Parse("official", App("ca.kazo.word", "Kazo Word", "0.1.0", Version("0.1.0")));
        AppCatalogListing mirror = Parse("mirror",
            App("ca.kazo.word", "Kazo Word (mirror)", "0.1.0", Version("0.1.0")),
            App("org.example.docs", "Docs", "1.0.0", Version("1.0.0")));

        IReadOnlyList<AppIntentSuggestedCatalogApp> apps = Select([("official", official), ("mirror", mirror)]);

        Assert.Equal(
            [new AppIntentSuggestedCatalogApp("ca.kazo.word", "Kazo Word", "official"), new AppIntentSuggestedCatalogApp("org.example.docs", "Docs", "mirror")],
            apps);
    }

    [Fact]
    public void At_most_eight_apps_are_proposed()
    {
        AppCatalogListing official = Parse("official", [.. Enumerable.Range(0, 12).Select(i => App($"org.example.app{i}", $"App {i}", "1.0.0", Version("1.0.0")))]);

        Assert.Equal(CatalogAppSuggestions.MaximumSuggestions, Select([("official", official)]).Count);
    }

    [Fact]
    public async Task Only_a_catalog_already_downloaded_is_read_whatever_its_age()
    {
        CountingDownloader downloader = new() { Catalog = CatalogBytes(App("ca.kazo.word", "Kazo Word", "0.1.0", Version("0.1.0"))) };
        DateTimeOffset now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        AppPackageInstaller installer = new(downloader, sdkVersion: Sdk, clock: () => now);
        AppSourcesConfig sources = AppSourcesConfigDocument.Read(null, AppPackageDefaults.Standard);

        Assert.Null(installer.GetCachedCatalog(AppSourcesConfig.OfficialCatalogName, sources));
        Assert.Equal(0, downloader.Requests);

        await installer.RefreshCatalogsAsync(sources);
        now += AppPackageInstaller.CatalogCacheLifetime * 10;
        AppCatalogListing? cached = installer.GetCachedCatalog(AppSourcesConfig.OfficialCatalogName, sources);

        Assert.Equal("ca.kazo.word", Assert.Single(cached!.Apps).AppId);
        Assert.Equal(1, downloader.Requests);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_downloaded_is_skipped_by_the_refresh()
    {
        CountingDownloader downloader = new() { Catalog = null };
        AppPackageInstaller installer = new(downloader, sdkVersion: Sdk);
        AppSourcesConfig sources = AppSourcesConfigDocument.Read(null, AppPackageDefaults.Standard);

        await installer.RefreshCatalogsAsync(sources);

        Assert.Null(installer.GetCachedCatalog(AppSourcesConfig.OfficialCatalogName, sources));
    }

    /// <summary>Serves the official <c>catalog.json</c> and counts the requests.</summary>
    private sealed class CountingDownloader : IAppPackageDownloader
    {
        public byte[]? Catalog { get; init; }

        public int Requests { get; private set; }

        public ValueTask<byte[]?> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken = default)
        {
            Requests++;
            return ValueTask.FromResult(uri == Address ? Catalog : null);
        }
    }
}
