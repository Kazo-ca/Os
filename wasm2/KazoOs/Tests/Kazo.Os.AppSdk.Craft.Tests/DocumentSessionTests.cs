using System.Text.Json;

namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>
/// The decisions of opening and saving (section 6 of the integration plan, "Save", "Modified" and
/// "Close"): one test per row of the "Save" table, the failed write, the cancelled dialog, the
/// export, two documents in one page, the title and the close.
/// </summary>
/// <remarks>
/// The <c>save</c> messages follow what WordCraft 0.18.1 sends, checked in its page: an untitled
/// document's first save carries a new name with <c>saveAs</c> true, the next ones the same name with
/// <c>saveAs</c> false; Save As to .odt is true; Save As to .pdf (a copy) is false.
/// </remarks>
public sealed class DocumentSessionTests
{
    private const string Report = "/home/neo/Documents/report.docx";

    private static DocumentSession NewSession() => new(SampleFormats.Word, "Kazo Word");

    [Fact]
    public void A_bound_name_saved_without_saveAs_rewrites_its_file()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);

        SaveDecision decision = session.Decide("report.docx", saveAs: false);

        Assert.Equal(SaveAction.Rewrite, decision.Action);
        Assert.Equal(Report, decision.Path);
        Assert.False(decision.AsksForPath);
    }

    [Fact]
    public void Save_as_asks_for_a_path_and_binds_the_document_to_it()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);

        SaveDecision decision = session.Decide("report.odt", saveAs: true);
        Assert.Equal(SaveAction.AskAndBind, decision.Action);
        Assert.Equal("/home/neo/Documents", decision.Folder);

        session.Saved(decision, "/home/neo/Documents/report.odt");
        Assert.Equal(SaveAction.Rewrite, session.Decide("report.odt", saveAs: false).Action);
        Assert.Equal("report.odt", session.CurrentName);
    }

    [Fact]
    public void Save_as_on_the_same_name_still_asks()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);

        Assert.Equal(SaveAction.AskAndBind, session.Decide("report.docx", saveAs: true).Action);
    }

    [Fact]
    public void An_untitled_document_asks_at_its_first_save_then_rewrites()
    {
        DocumentSession session = NewSession();

        SaveDecision first = session.Decide("Hello.docx", saveAs: true);
        Assert.Equal(SaveAction.AskAndBind, first.Action);
        Assert.Null(first.Folder);

        session.Saved(first, "/home/neo/letter.docx");
        SaveDecision second = session.Decide("Hello.docx", saveAs: false);
        Assert.Equal(SaveAction.Rewrite, second.Action);
        Assert.Equal("/home/neo/letter.docx", second.Path);
    }

    [Fact]
    public void A_name_never_bound_asks_even_without_saveAs()
    {
        DocumentSession session = NewSession();

        Assert.Equal(SaveAction.AskAndBind, session.Decide("imported.docx", saveAs: false).Action);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("report.png")]
    [InlineData("report.md")]
    [InlineData("report.txt")]
    [InlineData("report.html")]
    public void An_export_asks_for_a_path_and_keeps_the_binding(string copy)
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);

        SaveDecision decision = session.Decide(copy, saveAs: false);
        Assert.Equal(SaveAction.AskForCopy, decision.Action);
        Assert.Equal("/home/neo/Documents", decision.Folder);

        session.Saved(decision, "/home/neo/Documents/" + copy);
        Assert.Equal(Report, session.PathOf("report.docx"));
        Assert.Null(session.PathOf(copy));
        Assert.Equal("report.docx", session.CurrentName);
    }

    [Fact]
    public void A_failed_write_keeps_the_window_modified_and_the_next_save_asks_again()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);
        session.SetFrameDirty(true);

        SaveDecision decision = session.Decide("report.docx", saveAs: false);
        // WordCraft clears its own state as soon as it hands the save over.
        session.SetFrameDirty(false);
        session.SaveFailed(decision);

        Assert.True(session.IsDirty);
        Assert.StartsWith("*", session.Title("Untitled"));
        Assert.Equal(SaveAction.AskAndBind, session.Decide("report.docx", saveAs: false).Action);
    }

    [Fact]
    public void A_cancelled_dialog_keeps_the_document_modified_until_a_save_succeeds()
    {
        DocumentSession session = NewSession();
        SaveDecision decision = session.Decide("Hello.docx", saveAs: true);
        session.SaveFailed(decision);
        Assert.True(session.IsDirty);

        SaveDecision again = session.Decide("Hello.docx", saveAs: false);
        Assert.Equal(SaveAction.AskAndBind, again.Action);
        session.Saved(again, "/home/neo/hello.docx");

        Assert.False(session.IsDirty);
        Assert.Empty(session.UnsavedNames);
    }

    [Fact]
    public void A_failed_export_does_not_mark_the_document()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);

        session.SaveFailed(session.Decide("report.pdf", saveAs: false));

        Assert.False(session.IsDirty);
        Assert.Empty(session.UnsavedNames);
    }

    [Fact]
    public void Two_documents_in_one_page_keep_their_own_files()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);
        SaveDecision second = session.Decide("Notes.docx", saveAs: true);
        session.Saved(second, "/home/neo/notes.docx");

        Assert.Equal(Report, session.Decide("report.docx", saveAs: false).Path);
        Assert.Equal("/home/neo/notes.docx", session.Decide("Notes.docx", saveAs: false).Path);
    }

    [Fact]
    public void The_close_lists_the_documents_the_app_says_are_modified_and_those_whose_save_failed()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);
        session.SaveFailed(session.Decide("report.docx", saveAs: true));
        OpenDocument[] documents =
        [
            new(1, "report.docx", "report", Dirty: false, Active: false),
            new(2, null, "Document2", Dirty: true, Active: true),
            new(3, "clean.docx", "clean", Dirty: false, Active: false),
        ];

        IReadOnlyList<OpenDocument> unsaved = session.WithUnsavedWork(documents);

        Assert.Equal([1L, 2L], unsaved.Select(document => document.Id));
    }

    [Fact]
    public void Nothing_to_save_closes_at_once()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);

        Assert.Empty(session.WithUnsavedWork([new(1, "report.docx", "report", Dirty: false, Active: true)]));
    }

    [Fact]
    public void The_title_names_the_file_and_marks_changes()
    {
        DocumentSession session = NewSession();
        Assert.Equal("Kazo Word", session.Title("Untitled", hasDocument: false));
        Assert.Equal("Untitled — Kazo Word", session.Title("Untitled"));

        session.Opened("report.docx", Report);
        Assert.Equal("report.docx — Kazo Word", session.Title("Untitled"));

        session.SetFrameDirty(true);
        Assert.Equal("*report.docx — Kazo Word", session.Title("Untitled"));
    }

    [Fact]
    public void The_title_shows_the_chosen_file_when_WordCraft_keeps_its_own_name()
    {
        DocumentSession session = NewSession();
        session.Saved(session.Decide("Hello.docx", saveAs: true), "/home/neo/letter.docx");

        Assert.Equal("letter.docx — Kazo Word", session.Title("Untitled"));
    }

    [Fact]
    public void A_rename_moves_the_binding_and_the_unsaved_mark()
    {
        DocumentSession session = NewSession();
        session.Saved(session.Decide("Hello.docx", saveAs: true), "/home/neo/letter.docx");

        session.Renamed("Hello.docx", "letter.docx");

        Assert.Null(session.PathOf("Hello.docx"));
        Assert.Equal(SaveAction.Rewrite, session.Decide("letter.docx", saveAs: false).Action);
        Assert.Equal("letter.docx", session.CurrentName);
    }

    [Fact]
    public void A_blank_window_is_untouched_until_something_happens_to_it()
    {
        DocumentSession session = NewSession();
        Assert.True(session.IsUntouchedBlank);

        session.SetFrameDirty(false);
        Assert.True(session.IsUntouchedBlank);

        session.SetFrameDirty(true);
        Assert.False(session.IsUntouchedBlank);
    }

    [Fact]
    public void An_opened_file_makes_the_window_touched()
    {
        DocumentSession session = NewSession();
        session.Opened("report.docx", Report);

        Assert.False(session.IsUntouchedBlank);
    }

    [Fact]
    public void Ui_inspect_documents_are_read_from_the_app_s_answer()
    {
        using JsonDocument answer = JsonDocument.Parse("""
            {"ui":{},"documents":[
              {"active":true,"dirty":true,"id":10,"path":null,"title":"Document1"},
              {"active":false,"dirty":false,"id":11,"path":"foo.docx","title":"foo"},
              {"title":"no id"}
            ]}
            """);

        IReadOnlyList<OpenDocument> documents = OpenDocument.ListFrom(answer.RootElement);

        Assert.Equal(
            [new OpenDocument(10, null, "Document1", true, true), new OpenDocument(11, "foo.docx", "foo", false, false)],
            documents);
    }

    [Fact]
    public void An_answer_without_documents_lists_none()
    {
        using JsonDocument answer = JsonDocument.Parse("""{"ui":{}}""");

        Assert.Empty(OpenDocument.ListFrom(answer.RootElement));
        Assert.Empty(OpenDocument.ListFrom(default));
    }
}
