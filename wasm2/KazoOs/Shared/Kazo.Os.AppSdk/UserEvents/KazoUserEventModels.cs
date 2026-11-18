using System.Diagnostics.CodeAnalysis;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.AppSdk.UserEvents;

/// <summary>How serious a user event is (ADR 0254, section 2).</summary>
public enum KazoEventSeverity
{
    /// <summary>Something happened as expected: a program was installed, a machine started.</summary>
    Info = 0,

    /// <summary>Something the user may want to look at: a permission was refused, a conflict was resolved.</summary>
    Warning = 1,

    /// <summary>Something failed for the user: a download or an update did not finish.</summary>
    Error = 2
}

/// <summary>
/// What a user event is about (ADR 0254, section 4), so the user finds everything that happened to
/// one machine, program or file: a viewer groups the events by app, <see cref="Type"/> and <see cref="Id"/>.
/// </summary>
/// <param name="Type">Kind of subject, free but preferably <c>vm</c>, <c>program</c>, <c>prefix</c>, <c>file</c> or <c>app</c>.</param>
/// <param name="Id">Stable identifier of the subject (a machine's GUID, a program's path and hash), which never changes with its name.</param>
/// <param name="DisplayName">Name of the subject when the event happened; it may change later.</param>
public sealed record KazoEventSubject(string Type, string Id, string DisplayName);

/// <summary>
/// One event an app writes to the journal of the user it runs for, with <see cref="KazoEvents.Write"/>
/// (ADR 0254, section 2). An event names something of the user's (a file, a program, a destination,
/// a machine); an event without personal data goes to the app's log (<c>AppContext.Logging</c>) instead.
/// </summary>
/// <param name="Kind">Stable dotted identifier of the kind of event, such as <c>x86.vm.started</c>; one token, no whitespace.</param>
/// <param name="Message">Readable text, already localized by the app.</param>
/// <param name="Subject">What the event is about, or <see langword="null"/>.</param>
/// <param name="Severity">How serious the event is.</param>
/// <param name="Properties">
/// Extra details. The keys of <see cref="KazoUserEventProperties"/> are written by the OS and refused here.
/// </param>
public sealed record KazoUserEvent(
    string Kind,
    string Message,
    KazoEventSubject? Subject = null,
    KazoEventSeverity Severity = KazoEventSeverity.Info,
    IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>
/// Keys of the property block the OS writes on each line of a user's event log (ADR 0254, section 2).
/// An app cannot use them in <see cref="KazoUserEvent.Properties"/>.
/// </summary>
public static class KazoUserEventProperties
{
    /// <summary>The <see cref="KazoUserEvent.Kind"/>.</summary>
    public const string Kind = "kind";

    /// <summary>The <see cref="KazoEventSubject.Type"/>.</summary>
    public const string SubjectType = "subject.type";

    /// <summary>The <see cref="KazoEventSubject.Id"/>.</summary>
    public const string SubjectId = "subject.id";

    /// <summary>The <see cref="KazoEventSubject.DisplayName"/>.</summary>
    public const string SubjectName = "subject.name";

    /// <summary>The app that launched the writer or opened a file with it, when the OS knows it (ADR 0253).</summary>
    public const string Sender = "sender";

    /// <summary>Gets whether <paramref name="key"/> is written by the OS, so an app cannot set it.</summary>
    /// <param name="key">Property key.</param>
    public static bool IsReserved(string key) =>
        key is Kind or SubjectType or SubjectId or SubjectName or Sender;
}

/// <summary>
/// One line of a user's event log read back (ADR 0254, section 3): what the app wrote, plus what the
/// OS stamped — the time, the app, its process and the sender.
/// </summary>
/// <param name="TimestampUtc">When the event was written.</param>
/// <param name="AppId">App that wrote the event, set by the OS; <c>org.kazoos.*</c> for the OS's own events.</param>
/// <param name="ProcessId">Process of the app, when known.</param>
/// <param name="Kind">Kind of event.</param>
/// <param name="Message">Readable text.</param>
/// <param name="Subject">What the event is about, or <see langword="null"/>.</param>
/// <param name="Severity">How serious the event is.</param>
/// <param name="Properties">Extra details of the app, without the OS's own keys.</param>
/// <param name="SenderAppId">App that launched the writer or opened a file with it, or <see langword="null"/>.</param>
public sealed record KazoUserEventEntry(
    DateTimeOffset TimestampUtc,
    string AppId,
    long? ProcessId,
    string Kind,
    string Message,
    KazoEventSubject? Subject,
    KazoEventSeverity Severity,
    IReadOnlyDictionary<string, string> Properties,
    string? SenderAppId)
{
    /// <summary>
    /// Reads one line of <c>/var/log/users/&lt;id&gt;/events.log</c>. Never throws: a blank, foreign or
    /// truncated line returns <see langword="false"/>.
    /// </summary>
    /// <param name="line">Line to read, in the format of the OS's logs (ADR 0045).</param>
    /// <param name="entry">The event, when the line is one.</param>
    public static bool TryParse(string? line, [NotNullWhen(true)] out KazoUserEventEntry? entry)
    {
        entry = null;
        if (!LogLineFormat.TryParse(line, out LogLine? parsed)
            || !parsed.Properties.TryGetValue(KazoUserEventProperties.Kind, out string? kind))
        {
            return false;
        }

        KazoEventSubject? subject = parsed.Properties.TryGetValue(KazoUserEventProperties.SubjectType, out string? type)
            && parsed.Properties.TryGetValue(KazoUserEventProperties.SubjectId, out string? id)
            ? new KazoEventSubject(type, id, parsed.Properties.GetValueOrDefault(KazoUserEventProperties.SubjectName) ?? string.Empty)
            : null;
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        foreach ((string key, string value) in parsed.Properties)
        {
            if (!KazoUserEventProperties.IsReserved(key))
            {
                properties[key] = value;
            }
        }

        entry = new KazoUserEventEntry(
            parsed.TimestampUtc,
            parsed.Source,
            parsed.ProcessId,
            kind,
            parsed.Message,
            subject,
            parsed.Severity switch
            {
                DiagnosticSeverity.Warning => KazoEventSeverity.Warning,
                DiagnosticSeverity.Error or DiagnosticSeverity.Critical => KazoEventSeverity.Error,
                _ => KazoEventSeverity.Info
            },
            properties,
            parsed.Properties.GetValueOrDefault(KazoUserEventProperties.Sender));
        return true;
    }
}

/// <summary>
/// Which events to read back (ADR 0254, section 3). Every criterion left <see langword="null"/>
/// matches everything; the newest <see cref="Limit"/> events are returned, oldest first.
/// </summary>
public sealed record KazoUserEventFilter
{
    /// <summary>The most events one read returns.</summary>
    public const int MaximumLimit = 5000;

    /// <summary>Gets the kind of event, or a prefix ending in <c>.</c> (<c>x86.vm.</c>) for a family of kinds.</summary>
    public string? Kind { get; init; }

    /// <summary>Gets the subject type.</summary>
    public string? SubjectType { get; init; }

    /// <summary>Gets the subject id; usually set with <see cref="SubjectType"/>.</summary>
    public string? SubjectId { get; init; }

    /// <summary>Gets the lowest severity returned.</summary>
    public KazoEventSeverity? MinimumSeverity { get; init; }

    /// <summary>Gets the oldest time returned.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Gets the app that wrote the events; <see cref="KazoEvents.ReadAsync"/> always narrows it to the caller.</summary>
    public string? AppId { get; init; }

    /// <summary>Gets text the message, the kind or the subject's name must contain, ignoring case.</summary>
    public string? Search { get; init; }

    /// <summary>Gets the most events returned, the newest ones; from 1 to <see cref="MaximumLimit"/>.</summary>
    public int Limit { get; init; } = 1000;

    /// <summary>Gets whether <paramref name="entry"/> passes every criterion but <see cref="Limit"/>.</summary>
    /// <param name="entry">Event read back.</param>
    public bool Matches(KazoUserEventEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Kind is { Length: > 0 } kind
            && !(kind.EndsWith('.')
                ? entry.Kind.StartsWith(kind, StringComparison.Ordinal)
                : string.Equals(entry.Kind, kind, StringComparison.Ordinal)))
        {
            return false;
        }

        if ((SubjectType is not null && !string.Equals(entry.Subject?.Type, SubjectType, StringComparison.Ordinal))
            || (SubjectId is not null && !string.Equals(entry.Subject?.Id, SubjectId, StringComparison.Ordinal))
            || (AppId is not null && !string.Equals(entry.AppId, AppId, StringComparison.Ordinal))
            || (MinimumSeverity is { } minimum && entry.Severity < minimum)
            || (Since is { } since && entry.TimestampUtc < since))
        {
            return false;
        }

        return Search is not { Length: > 0 } search
            || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Kind.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (entry.Subject?.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Keeps the entries that match, the newest <see cref="Limit"/> of them, in the order given (oldest first).
    /// </summary>
    /// <param name="entries">Events in chronological order.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="Limit"/> is out of range.</exception>
    public IReadOnlyList<KazoUserEventEntry> Apply(IEnumerable<KazoUserEventEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        EnsureValidLimit();
        List<KazoUserEventEntry> matching = [.. entries.Where(Matches)];
        return matching.Count <= Limit ? matching : matching.GetRange(matching.Count - Limit, Limit);
    }

    /// <summary>Throws when <see cref="Limit"/> is out of range, before any file is read.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="Limit"/> is below 1 or above <see cref="MaximumLimit"/>.</exception>
    public void EnsureValidLimit()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1, nameof(Limit));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limit, MaximumLimit, nameof(Limit));
    }
}

/// <summary>
/// The journal of the user an app instance runs for (ADR 0254), behind <see cref="KazoEvents"/>.
/// Both members need the capability <c>events.write</c>.
/// </summary>
public interface IAppUserEventsGateway
{
    /// <summary>Writes one event to the journal of the user the app runs for.</summary>
    /// <param name="userEvent">Event to write.</param>
    void Write(KazoUserEvent userEvent);

    /// <summary>Reads back this app's own events from that journal, oldest first.</summary>
    /// <param name="filter">Which events to return; its <see cref="KazoUserEventFilter.AppId"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<KazoUserEventEntry>> ReadAsync(KazoUserEventFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>The <see cref="IAppUserEventsGateway"/> of a host without a user journal.</summary>
public sealed class UnsupportedAppUserEventsGateway : IAppUserEventsGateway
{
    private UnsupportedAppUserEventsGateway()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static UnsupportedAppUserEventsGateway Instance { get; } = new();

    /// <inheritdoc />
    public void Write(KazoUserEvent userEvent) =>
        throw new NotSupportedException("This host keeps no user event journal.");

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoUserEventEntry>> ReadAsync(KazoUserEventFilter filter, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host keeps no user event journal.");
}
