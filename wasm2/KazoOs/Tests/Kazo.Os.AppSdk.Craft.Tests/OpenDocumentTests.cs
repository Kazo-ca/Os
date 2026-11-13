using System.Text.Json;

namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>The two shapes of <c>ui.inspect</c> the Craft apps give: WordCraft's and GridCraft's.</summary>
public sealed class OpenDocumentTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // GridCraft 0.19.1 names the next save of a workbook <title without extension>.xlsx.
    private static string? XlsxName(string title)
    {
        string stem = CraftDocumentFormats.ExtensionOf(title).Length > 0 ? title[..title.LastIndexOf('.')] : title;
        return stem + ".xlsx";
    }

    [Fact]
    public void GridCraft_lists_each_workbook_once_by_its_windows()
    {
        JsonElement inspect = Json("""
            {
              "documents": [ { "title": "Book1", "dirty": false }, { "title": "Budget.csv", "dirty": true } ],
              "activeDocument": 1,
              "windows": [
                { "window": 1, "doc": 7, "index": 0, "title": "Book1", "dirty": false, "active": false, "hidden": false },
                { "window": 2, "doc": 9, "index": 1, "title": "Budget.csv:1", "dirty": true, "active": false, "hidden": false },
                { "window": 3, "doc": 9, "index": 1, "title": "Budget.csv:2", "dirty": true, "active": true, "hidden": false }
              ]
            }
            """);

        Assert.Equal(
            [
                new OpenDocument(7, "Book1.xlsx", "Book1", Dirty: false, Active: false),
                new OpenDocument(9, "Budget.xlsx", "Budget.csv", Dirty: true, Active: true),
            ],
            OpenDocument.ListFrom(inspect, XlsxName));
    }

    [Fact]
    public void Without_a_name_rule_a_GridCraft_workbook_has_no_name()
    {
        JsonElement inspect = Json("""{ "documents": [ { "title": "Book1", "dirty": true } ], "windows": [ { "doc": 3, "index": 0 } ] }""");

        Assert.Equal([new OpenDocument(3, null, "Book1", Dirty: true, Active: false)], OpenDocument.ListFrom(inspect));
    }

    [Fact]
    public void Malformed_GridCraft_windows_are_skipped()
    {
        JsonElement inspect = Json("""
            {
              "documents": [ { "title": "Book1", "dirty": true }, "not a document" ],
              "windows": [
                { "doc": "7", "index": 0 },
                { "doc": 7, "index": 5 },
                { "doc": 7, "index": -1 },
                { "doc": 7.5, "index": 0 },
                { "doc": 7 },
                "window",
                { "doc": 8, "index": 1 },
                { "doc": 9, "index": 0, "active": true }
              ]
            }
            """);

        Assert.Equal(
            [new OpenDocument(8, null, string.Empty, Dirty: false, Active: false), new OpenDocument(9, null, "Book1", Dirty: true, Active: true)],
            OpenDocument.ListFrom(inspect));
    }

    [Fact]
    public void WordCraft_ids_take_precedence_over_windows_and_keep_their_path()
    {
        JsonElement inspect = Json("""
            { "documents": [ { "id": 1, "path": "a.docx", "title": "a", "dirty": true, "active": true }, { "id": 2, "path": null, "title": "Document2" } ],
              "windows": [ { "doc": 5, "index": 0 } ] }
            """);

        Assert.Equal(
            [new OpenDocument(1, "a.docx", "a", true, true), new OpenDocument(2, "Document2.xlsx", "Document2", false, false)],
            OpenDocument.ListFrom(inspect, XlsxName));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{ "documents": {} }""")]
    [InlineData("""{ "documents": [ { "id": "1" }, { "id": 1.5 } ] }""")]
    [InlineData("""{ "documents": [ { "title": "Book1" } ], "windows": {} }""")]
    public void An_unknown_shape_lists_nothing(string text)
    {
        Assert.Empty(OpenDocument.ListFrom(Json(text), XlsxName));
    }
}
