namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>
/// The module with a Craft app that behaves like GridCraft 0.19.1 rather than WordCraft: every
/// <c>save</c> has <c>saveAs</c> false and names the file with the extension of the format it wrote
/// (an opened <c>Budget.ods</c> is saved as <c>Budget.xlsx</c>), "Save As" is the window's (a reserved
/// chord), <c>dirty</c> covers every workbook of the page, and one extension (<c>.csv</c>) both opens
/// and is written as a copy.
/// </summary>
public sealed class SpreadsheetFlowTests
{
    private static readonly CraftDocumentFormats Formats = new(
    [
        new(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", IsDefault: true, IsCopy: false),
        new(".xlsm", "application/vnd.ms-excel.sheet.macroenabled.12", IsDefault: true, IsCopy: false),
        new(".ods", "application/vnd.oasis.opendocument.spreadsheet", IsDefault: true, IsCopy: false),
        new(".csv", "text/csv", IsDefault: false, IsCopy: true),
        new(".pdf", "application/pdf", IsDefault: false, IsCopy: true, Opens: false),
    ]);

    private static DocumentSession NewSession() => new(Formats, "Kazo Spreadsheets");

    [Fact]
    public void A_workbook_opened_in_another_format_asks_once_then_rewrites_its_new_file()
    {
        DocumentSession session = NewSession();
        session.Opened("Budget.ods", "/home/neo/Budget.ods");

        SaveDecision first = session.Decide("Budget.xlsx", saveAs: false);
        Assert.Equal(SaveAction.AskAndBind, first.Action);
        Assert.Equal("/home/neo", first.Folder);

        session.Saved(first, "/home/neo/Budget.xlsx");
        Assert.Equal(SaveAction.Rewrite, session.Decide("Budget.xlsx", saveAs: false).Action);
        Assert.Equal("Budget.xlsx — Kazo Spreadsheets", session.Title("Untitled"));
    }

    [Fact]
    public void Save_as_started_by_the_window_asks_even_for_a_bound_file()
    {
        // F12 or Ctrl+Shift+S reach the window (target App); it fetches the bytes itself and
        // decides with saveAs true.
        ReservedKey saveAs = ReservedKeys.Parse("Ctrl+Shift+S", ReservedKeyTarget.App);
        Assert.Equal(new ReservedKey("s", "KeyS", true, false, true, false, ReservedKeyTarget.App), saveAs);
        Assert.Equal(new ReservedKey("F12", null, false, false, false, false, ReservedKeyTarget.App), ReservedKeys.Parse("F12", ReservedKeyTarget.App));

        DocumentSession session = NewSession();
        session.Opened("Budget.xlsx", "/home/neo/Budget.xlsx");

        Assert.Equal(SaveAction.AskAndBind, session.Decide("Budget.xlsx", saveAs: true).Action);
    }

    [Fact]
    public void A_csv_opens_but_saving_it_is_an_export()
    {
        Assert.Contains(".csv", Formats.OpenExtensions);
        DocumentSession session = NewSession();
        session.Opened("Data.csv", "/home/neo/Data.csv");

        Assert.Equal(SaveAction.AskForCopy, session.Decide("Data.csv", saveAs: false).Action);
        Assert.Equal(SaveAction.AskAndBind, session.Decide("Data.xlsx", saveAs: false).Action);
    }

    [Fact]
    public void Dirty_for_the_whole_page_marks_the_window()
    {
        DocumentSession session = NewSession();
        session.Opened("Budget.xlsx", "/home/neo/Budget.xlsx");

        session.SetFrameDirty(true);

        Assert.True(session.IsDirty);
        Assert.StartsWith("*", session.Title("Untitled"));
    }
}
