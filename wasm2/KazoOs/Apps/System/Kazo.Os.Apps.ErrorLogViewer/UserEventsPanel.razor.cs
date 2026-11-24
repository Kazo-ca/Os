using Kazo.Os.AppSdk.UserEvents;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.ErrorLogViewer;

/// <summary>
/// The "My events" tab of the viewer (ADR 0254, section 3): one user's event journal, newest first,
/// filtered by app, severity, subject (a machine, a program) and text. The window reads the journal;
/// this panel only filters and shows it, so it renders the same way for an administrator who picked
/// another user.
/// </summary>
public partial class UserEventsPanel
{
    /// <summary>Most rows shown; the newest are kept.</summary>
    public const int MaxDisplayedRows = 2000;

    /// <summary>Gets or sets the journal's events, oldest first, as read from the file.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<KazoUserEventEntry> Entries { get; set; } = [];

    /// <summary>Gets or sets the viewer's texts.</summary>
    [Parameter]
    [EditorRequired]
    public IStringLocalizer L { get; set; } = default!;

    /// <summary>Gets or sets controls shown before the filters, such as an administrator's user picker.</summary>
    [Parameter]
    public RenderFragment? Toolbar { get; set; }

    private string SelectedApp { get; set; } = string.Empty;

    private string SelectedSeverity { get; set; } = string.Empty;

    private string SelectedSubject { get; set; } = string.Empty;

    private string Search { get; set; } = string.Empty;

    /// <summary>Gets the apps that wrote at least one event, in order.</summary>
    private IEnumerable<string> Apps =>
        Entries.Select(static entry => entry.AppId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>Gets each subject once, grouped by app, type and id like the ADR asks (its latest name shown).</summary>
    private IEnumerable<SubjectChoice> Subjects =>
        Entries
            .Where(static entry => entry.Subject is not null)
            .GroupBy(static entry => SubjectKey(entry), StringComparer.Ordinal)
            .Select(static group =>
            {
                KazoUserEventEntry latest = group.Last();
                return new SubjectChoice(group.Key, $"{latest.Subject!.DisplayName} ({latest.Subject.Type}, {latest.AppId})");
            })
            .OrderBy(static choice => choice.Label, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Gets the events that pass the filters, newest first.</summary>
    private IReadOnlyList<KazoUserEventEntry> Visible
    {
        get
        {
            KazoUserEventFilter filter = new()
            {
                AppId = SelectedApp.Length == 0 ? null : SelectedApp,
                MinimumSeverity = Enum.TryParse(SelectedSeverity, out KazoEventSeverity severity) ? severity : null,
                Search = Search.Length == 0 ? null : Search,
            };
            return [.. Entries
                .Where(entry => filter.Matches(entry)
                    && (SelectedSubject.Length == 0 || string.Equals(SubjectKey(entry), SelectedSubject, StringComparison.Ordinal)))
                .Reverse()
                .Take(MaxDisplayedRows)];
        }
    }

    /// <summary>Identifies a subject the way the viewer groups them: app, type and stable id.</summary>
    /// <param name="entry">Event read back.</param>
    public static string SubjectKey(KazoUserEventEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Subject is { } subject ? $"{entry.AppId}\u001f{subject.Type}\u001f{subject.Id}" : string.Empty;
    }

    private string SeverityText(KazoEventSeverity severity) => L[$"Events.Severity.{severity}"];

    private static string SubjectTitle(KazoUserEventEntry entry) =>
        entry.Subject is { } subject ? $"{subject.Type}: {subject.Id}" : string.Empty;

    /// <summary>One entry of the subject picker.</summary>
    private sealed record SubjectChoice(string Key, string Label);
}
