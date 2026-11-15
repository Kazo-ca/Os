using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>An app's list of formats, the manifest's file handlers, the paths and the launch arguments.</summary>
public sealed class CraftDocumentFormatsTests
{
    [Theory]
    [InlineData(".docx")]
    [InlineData(".doc")]
    [InlineData(".odt")]
    [InlineData(".rtf")]
    public void The_app_s_own_documents_are_opened_and_edited(string extension)
    {
        FileHandler handler = Assert.Single(SampleFormats.Word.FileHandlers, candidate => candidate.Extensions.Contains(extension));

        Assert.Equal(["open", "edit"], handler.Actions);
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".txt")]
    [InlineData(".tex")]
    public void Other_formats_are_offered_in_Open_with_only(string extension)
    {
        FileHandler handler = Assert.Single(SampleFormats.Word.FileHandlers, candidate => candidate.Extensions.Contains(extension));

        Assert.Equal(["open"], handler.Actions);
    }

    [Fact]
    public void Each_media_type_has_one_handler_and_export_only_formats_have_none()
    {
        IReadOnlyList<FileHandler> handlers = SampleFormats.Word.FileHandlers;

        Assert.Equal(handlers.Count, handlers.Select(handler => handler.MediaType).Distinct().Count());
        Assert.Equal([".doc", ".dot"], Assert.Single(handlers, handler => handler.MediaType == "application/msword").Extensions);
        Assert.DoesNotContain(handlers, handler => handler.Extensions.Contains(".pdf") || handler.Extensions.Contains(".png"));
        Assert.DoesNotContain(".pdf", SampleFormats.Word.OpenExtensions);
        Assert.Contains(".docx", SampleFormats.Word.OpenExtensions);
    }

    [Fact]
    public void Extensions_are_kept_lowercase()
    {
        CraftDocumentFormats formats = new([new(".XLSX", "application/x-sheet", IsDefault: true, IsCopy: false)]);

        Assert.Equal(".xlsx", formats.All[0].Extension);
        Assert.NotNull(formats.Find("Book.xlsx"));
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData(".")]
    [InlineData("")]
    public void An_extension_without_its_dot_is_refused(string extension)
    {
        Assert.Throws<ArgumentException>(() => new CraftDocumentFormats([new(extension, "text/plain", IsDefault: true, IsCopy: false)]));
    }

    [Fact]
    public void An_extension_listed_twice_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new CraftDocumentFormats(
        [
            new(".csv", "text/csv", IsDefault: true, IsCopy: false),
            new(".CSV", "text/csv", IsDefault: false, IsCopy: true),
        ]));
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("REPORT.PDF", true)]
    [InlineData("page.png", true)]
    [InlineData("notes.md", true)]
    [InlineData("page.htm", true)]
    [InlineData("report.docx", false)]
    [InlineData("report.tex", false)]
    [InlineData("unknown.xyz", false)]
    [InlineData("no-extension", false)]
    public void Copies_are_told_by_their_extension(string name, bool copy)
    {
        Assert.Equal(copy, SampleFormats.Word.IsCopy(name));
    }

    [Theory]
    [InlineData("/home/neo/letter.docx", "Hello.docx", "/home/neo/letter.docx")]
    [InlineData("/home/neo/letter", "Hello.docx", "/home/neo/letter.docx")]
    [InlineData("/home/neo/letter.odt", "Hello.docx", "/home/neo/letter.odt.docx")]
    [InlineData("/home/neo/LETTER.DOCX", "Hello.docx", "/home/neo/LETTER.DOCX")]
    [InlineData("/home/neo/letter", "no-extension", "/home/neo/letter")]
    public void A_chosen_path_keeps_the_format_the_app_wrote(string chosen, string name, string written)
    {
        Assert.Equal(written, CraftDocumentFormats.WithExtensionOf(chosen, name));
    }

    [Theory]
    [InlineData("/home/neo/report.docx", "/home/neo", "report.docx", ".docx")]
    [InlineData("/report.docx", "/", "report.docx", ".docx")]
    [InlineData("report.docx", null, "report.docx", ".docx")]
    [InlineData("/home/neo/.hidden", "/home/neo", ".hidden", "")]
    public void Paths_split_into_folder_name_and_extension(string path, string? folder, string name, string extension)
    {
        Assert.Equal(folder, CraftDocumentFormats.FolderOf(path));
        Assert.Equal(name, CraftDocumentFormats.FileNameOf(path));
        Assert.Equal(extension, CraftDocumentFormats.ExtensionOf(path));
    }

    [Fact]
    public void The_first_absolute_path_is_the_file_to_open()
    {
        OpenRequest request = OpenRequest.FromArguments(["--webgl", "/home/neo/report.docx", "/home/neo/other.docx"]);

        Assert.Equal("/home/neo/report.docx", request.Path);
        Assert.True(request.WebGl);
    }

    [Fact]
    public void A_plain_launch_opens_a_blank_document()
    {
        OpenRequest request = OpenRequest.FromArguments([]);

        Assert.Null(request.Path);
        Assert.False(request.WebGl);
    }
}
