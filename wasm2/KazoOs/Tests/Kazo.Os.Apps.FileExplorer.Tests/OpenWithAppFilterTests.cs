using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Kazo.Os.Apps.FileExplorer.Tests;

/// <summary>The search box and the category selector of the "Open with" box.</summary>
public sealed class OpenWithAppFilterTests
{
    private static readonly KazoAppInfo Browser = App("org.kazoos.browser", "Browser", "Browses the web", "internet");
    private static readonly KazoAppInfo Calculator = App("org.kazoos.calculator", "Calculator", "Computes numbers", "utilities");
    private static readonly KazoAppInfo CodeEditor = App("org.kazoos.code-editor", "Code Editor", "Edits source code", "developer");
    private static readonly KazoAppInfo TextEditor = App("org.kazoos.text-editor", "Text Editor", "Edits plain text", "utilities");
    private static readonly KazoAppInfo[] Apps = [Browser, Calculator, CodeEditor, TextEditor];

    [Fact]
    public void Apply_keeps_every_app_without_a_search_or_a_category()
    {
        Assert.Equal(Apps, OpenWithAppFilter.Apply(Apps, "  ", null));
        Assert.Equal(Apps, OpenWithAppFilter.Apply(Apps, null, string.Empty));
    }

    [Fact]
    public void Apply_matches_the_name_the_id_and_the_description_ignoring_case()
    {
        Assert.Equal([CodeEditor, TextEditor], OpenWithAppFilter.Apply(Apps, "EDITOR", null));
        Assert.Equal([Calculator], OpenWithAppFilter.Apply(Apps, "kazoos.calc", null));
        Assert.Equal([Browser], OpenWithAppFilter.Apply(Apps, "web", null));
    }

    [Fact]
    public void Apply_needs_every_word_of_the_search()
    {
        Assert.Equal([TextEditor], OpenWithAppFilter.Apply(Apps, "editor plain", null));
        Assert.Empty(OpenWithAppFilter.Apply(Apps, "editor numbers", null));
    }

    [Fact]
    public void Apply_keeps_only_the_chosen_category_and_combines_it_with_the_search()
    {
        Assert.Equal([Calculator, TextEditor], OpenWithAppFilter.Apply(Apps, null, "utilities"));
        Assert.Equal([TextEditor], OpenWithAppFilter.Apply(Apps, "edit", "Utilities"));
        Assert.Empty(OpenWithAppFilter.Apply(Apps, "browser", "utilities"));
    }

    [Fact]
    public void Categories_lists_each_category_once_by_its_label()
    {
        KazoAppInfo uncategorized = App("org.e2e.none", "None", string.Empty, string.Empty);

        IReadOnlyList<string> categories = OpenWithAppFilter.Categories([.. Apps, uncategorized], category => category switch
        {
            "developer" => "Development",
            "internet" => "Internet",
            _ => "Utilities"
        });

        Assert.Equal(["developer", "internet", "utilities"], categories);
    }

    [Fact]
    public void Category_labels_and_filter_texts_are_translated()
    {
        IStringLocalizer<Strings> french = ((IAppExecutionContext)new LocaleTestContext("fr_FR.UTF-8")).GetLocalizer<Strings>();
        IStringLocalizer<Strings> english = ((IAppExecutionContext)new LocaleTestContext("en_US.UTF-8")).GetLocalizer<Strings>();

        Assert.Equal("Utilitaires", french["Category.Utilities"]);
        Assert.Equal("Toutes les catégories", french["OpenWith.AllCategories"]);
        Assert.Equal("Development", english["Category.Developer"]);
        Assert.Equal("Search the installed apps", english["OpenWith.SearchLabel"]);
    }

    private static KazoAppInfo App(string id, string name, string description, string category) =>
        new(id, name, description, AppKind.Window, category, null, "1.0.0", "pub.kazoos", true, false, []);
}
