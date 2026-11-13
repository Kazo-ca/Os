using System.Globalization;
using Kazo.Os.AppSdk.Apps;
using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

public sealed class StoreCatalogTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private static KazoCatalogApp App(string catalog, string appId, string name, string category, string description = "", params string[] tags) =>
        new(catalog, appId, name, category, tags,
            new Dictionary<string, string> { ["en"] = description, ["fr"] = "fr " + description },
            "MIT", "https://github.com/example/" + name, "1.1.0",
            [new KazoCatalogAppVersion("1.1.0", 2048, ["storage.app"]), new KazoCatalogAppVersion("1.0.0", 1024, [])],
            KazoPackageTrust.Verified);

    private static KazoAppCatalog Catalog(string name, params KazoCatalogApp[] apps) =>
        new(name, KazoPackageTrust.Verified,
            [new KazoCatalogCategory("utilities", new Dictionary<string, string> { ["en"] = "Utilities", ["fr"] = "Utilitaires" }),
             new KazoCatalogCategory("graphics", new Dictionary<string, string> { ["en"] = "Graphics" })],
            apps);

    [Fact]
    public void An_app_listed_by_two_catalogs_shows_the_first_catalog()
    {
        StoreCatalog store = new(
            [Catalog("official", App("official", "org.example.notes", "Notes", "utilities")),
             Catalog("school", App("school", "org.example.notes", "Notes school", "utilities"))],
            [], [], new HashSet<string>());

        Assert.Equal("official", Assert.Single(store.Available).App!.Catalog);
    }

    [Fact]
    public void An_installed_copy_keeps_the_page_of_its_catalog()
    {
        StoreCatalog store = new(
            [Catalog("official", App("official", "org.example.notes", "Notes", "utilities")),
             Catalog("school", App("school", "org.example.notes", "Notes school", "utilities"))],
            [new KazoInstalledPackage("org.example.notes", "catalog:school/org.example.notes", "1.0.0", true)],
            [], new HashSet<string>());

        Assert.Equal("school", store.Find("org.example.notes")!.App!.Catalog);
    }

    [Fact]
    public void States_follow_install_update_and_restart()
    {
        StoreCatalog store = new(
            [Catalog("official",
                App("official", "org.example.a", "A", "utilities"),
                App("official", "org.example.b", "B", "utilities"),
                App("official", "org.example.c", "C", "utilities"),
                App("official", "org.example.d", "D", "utilities"),
                App("official", "org.example.e", "E", "utilities"))],
            [new KazoInstalledPackage("org.example.b", "catalog:official/org.example.b", "1.1.0", true),
             new KazoInstalledPackage("org.example.c", "catalog:official/org.example.c", "1.0.0", true),
             new KazoInstalledPackage("org.example.d", "catalog:official/org.example.d", "1.1.0", false),
             new KazoInstalledPackage("org.example.e", "catalog:official/org.example.e", "1.1.0", true)],
            [new KazoPackageUpdate("org.example.c", "catalog:official/org.example.c", "1.0.0", "1.1.0", true)],
            new HashSet<string> { "org.example.e" });

        Assert.Equal(StoreAppState.NotInstalled, store.Find("org.example.a")!.State);
        Assert.Equal(StoreAppState.Installed, store.Find("org.example.b")!.State);
        Assert.Equal(StoreAppState.UpdateAvailable, store.Find("org.example.c")!.State);
        Assert.Equal(StoreAppState.PendingRestart, store.Find("org.example.d")!.State);
        Assert.Equal(StoreAppState.PendingRestart, store.Find("org.example.e")!.State);
        Assert.True(store.HasPendingRestart);
        Assert.Equal("org.example.c", Assert.Single(store.Updatable).AppId);
        Assert.Equal(["B", "C", "D", "E"], store.Installed.Select(entry => entry.Name));
    }

    [Fact]
    public void A_system_copy_wins_over_a_personal_copy()
    {
        StoreCatalog store = new(
            [Catalog("official", App("official", "org.example.a", "A", "utilities"))],
            [new KazoInstalledPackage("org.example.a", "catalog:official/org.example.a", "1.0.0", true) { SystemWide = false },
             new KazoInstalledPackage("org.example.a", "catalog:official/org.example.a", "1.1.0", true) { SystemWide = true }],
            [], new HashSet<string>());

        Assert.True(store.Find("org.example.a")!.Installed!.SystemWide);
    }

    [Fact]
    public void An_installed_app_no_catalog_lists_is_still_in_the_installed_tab()
    {
        StoreCatalog store = new(
            [Catalog("official")],
            [new KazoInstalledPackage("org.example.gone", "github:example/gone", "1.0.0", true)],
            [], new HashSet<string>());

        StoreEntry entry = Assert.Single(store.Installed);
        Assert.Null(entry.App);
        Assert.Equal("org.example.gone", entry.Name);
        Assert.Empty(store.Available);
    }

    /// <summary>An app whose latest version was published at <paramref name="publishedAt"/>, or with no date.</summary>
    private static KazoCatalogApp Published(string appId, string name, string category, string? publishedAt)
    {
        KazoCatalogApp app = App("official", appId, name, category);
        if (publishedAt is null)
        {
            return app;
        }

        KazoCatalogAppVersion latest = new("1.1.0", 2048, []) { PublishedAt = DateTimeOffset.Parse(publishedAt, CultureInfo.InvariantCulture) };
        return app with { Versions = [latest] };
    }

    [Fact]
    public void Category_filters_count_the_apps_of_each_category_then_the_others()
    {
        StoreCatalog store = new(
            [Catalog("official",
                App("official", "org.example.a", "Alpha", "utilities"),
                App("official", "org.example.c", "Gamma", "utilities"),
                App("official", "org.example.b", "Beta", "unknown"))],
            [], [], new HashSet<string>());

        Assert.Equal([("utilities", 2), (StoreCatalog.OtherCategoryId, 1)], store.CategoryFilters());
        Assert.Equal("Beta", Assert.Single(store.Grid(StoreCatalog.OtherCategoryId)).Name);
        Assert.Equal(["Alpha", "Gamma"], store.Grid("utilities").Select(entry => entry.Name));
        Assert.Empty(store.Grid("graphics"));
        Assert.Equal(3, store.Grid(null).Count);
    }

    [Fact]
    public void The_grid_lists_the_newest_app_first_and_undated_apps_last_by_name()
    {
        StoreCatalog store = new(
            [Catalog("official",
                Published("org.example.old", "Old", "utilities", "2026-01-05T00:00:00Z"),
                Published("org.example.new", "New", "graphics", "2026-11-01T00:00:00Z"),
                Published("org.example.zed", "Zed", "utilities", null),
                Published("org.example.abc", "Abc", "utilities", null))],
            [], [], new HashSet<string>());

        Assert.Equal(["New", "Old", "Abc", "Zed"], store.Grid(null).Select(entry => entry.Name));
        Assert.Equal(["Old", "Abc", "Zed"], store.Grid("utilities").Select(entry => entry.Name));
    }

    [Fact]
    public void The_featured_app_is_the_newest_one_not_installed()
    {
        StoreCatalog store = new(
            [Catalog("official",
                Published("org.example.old", "Old", "utilities", "2026-01-05T00:00:00Z"),
                Published("org.example.mid", "Mid", "utilities", "2026-06-01T00:00:00Z"),
                Published("org.example.new", "New", "graphics", "2026-11-01T00:00:00Z"))],
            [new KazoInstalledPackage("org.example.new", "catalog:official/org.example.new", "1.1.0", true)],
            [], new HashSet<string>());

        Assert.Equal("Mid", store.Featured()!.Name);
    }

    [Fact]
    public void The_featured_app_is_the_newest_one_when_every_app_is_installed()
    {
        StoreCatalog store = new(
            [Catalog("official",
                Published("org.example.old", "Old", "utilities", "2026-01-05T00:00:00Z"),
                Published("org.example.new", "New", "graphics", "2026-11-01T00:00:00Z"))],
            [new KazoInstalledPackage("org.example.new", "catalog:official/org.example.new", "1.1.0", true),
             new KazoInstalledPackage("org.example.old", "catalog:official/org.example.old", "1.1.0", true)],
            [], new HashSet<string>());

        Assert.Equal("New", store.Featured()!.Name);
    }

    [Fact]
    public void No_app_means_no_featured_app_and_no_filter()
    {
        Assert.Null(StoreCatalog.Empty.Featured());
        Assert.Empty(StoreCatalog.Empty.CategoryFilters());
        Assert.Empty(StoreCatalog.Empty.Grid(null));
    }

    [Theory]
    [InlineData("notes", 1)]
    [InlineData("NOTÉS", 1)]
    [InlineData("markdown", 1)]
    [InlineData("fr quick", 1)]
    [InlineData("org.example", 2)]
    [InlineData("nothing", 0)]
    [InlineData("  ", 2)]
    public void Search_ignores_case_and_accents_and_matches_every_word(string query, int expected)
    {
        StoreCatalog store = new(
            [Catalog("official",
                App("official", "org.example.notes", "Notes", "utilities", "quick notes", "markdown"),
                App("official", "org.example.paint", "Paint", "graphics", "draw"))],
            [], [], new HashSet<string>());

        Assert.Equal(expected, store.Search(query, French).Count);
    }

    [Fact]
    public void Failed_catalogs_are_reported()
    {
        StoreCatalog store = new(
            [Catalog("official"), new KazoAppCatalog("school", KazoPackageTrust.Trusted, [], []) { Error = "offline" }],
            [], [], new HashSet<string>());

        Assert.Equal("school", Assert.Single(store.FailedCatalogs).Name);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1572864, "1.5 MB")]
    public void FormatSize_uses_bytes_KB_and_MB(long bytes, string expected)
    {
        Assert.Equal(expected, StoreCatalog.FormatSize(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_installed_tab_is_searched_too_even_for_an_app_no_catalog_lists()
    {
        StoreCatalog store = new(
            [Catalog("official",
                App("official", "org.example.notes", "Notes", "utilities", "Write things down"),
                App("official", "org.example.paint", "Paint", "graphics", "Draw pictures"))],
            [new KazoInstalledPackage("org.example.notes", "catalog:official/org.example.notes", "1.1.0", true),
             new KazoInstalledPackage("org.example.paint", "catalog:official/org.example.paint", "1.1.0", true),
             new KazoInstalledPackage("org.other.clock", "file:/clock.kazoapp", "1.0.0", true)],
            [], new HashSet<string>());

        Assert.Equal(3, store.SearchInstalled(null, French).Count);
        Assert.Equal("org.example.notes", Assert.Single(store.SearchInstalled("WRITE", French)).AppId);
        Assert.Equal("org.other.clock", Assert.Single(store.SearchInstalled("clock", French)).AppId);
        Assert.Empty(store.SearchInstalled("zzzz", French));
    }
}
