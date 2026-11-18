using Kazo.Os.AppSdk.UserEvents;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// Who wrote a user event, stamped by the OS on the line (ADR 0254, section 2): the app from its own
/// context (never from what the app passed), its process, and the app that launched it or opened a
/// file with it.
/// </summary>
/// <param name="AppId">App that wrote the event; an <c>org.kazoos.*</c> id for the OS's own events.</param>
/// <param name="ProcessId">Process of the app, when there is one.</param>
/// <param name="SenderAppId">App that launched the writer or opened a file with it (ADR 0253), when known.</param>
public sealed record UserEventAuthor(string AppId, long? ProcessId = null, string? SenderAppId = null);

/// <summary>
/// The account a user event belongs to: its stable identifier names the directory, and the file is
/// created as that user and their primary group (ADR 0254, section 1).
/// </summary>
/// <param name="UserId">Stable identifier of the account (ADR 0169).</param>
/// <param name="PrimaryGroupId">Primary group of the account.</param>
public sealed record UserEventOwner(LocalUserId UserId, LocalGroupId PrimaryGroupId)
{
    /// <summary>Gets the owner of a signed-in user.</summary>
    /// <param name="principal">Signed-in user.</param>
    public static UserEventOwner Of(AuthenticatedPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new UserEventOwner(principal.UserId, principal.PrimaryGroupId);
    }

    /// <summary>Gets the owner of an account.</summary>
    /// <param name="user">Local account.</param>
    public static UserEventOwner Of(LocalUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserEventOwner(user.Id, user.PrimaryGroupId);
    }
}

/// <summary>
/// The per-user event journals, <c>/var/log/users/&lt;id&gt;/events.log</c> (ADR 0254): written by
/// apps through <c>KazoEvents</c> and by the OS for its own personal actions, read back by the user,
/// by an app for its own lines, and by an administrator after the OS's authentication.
/// </summary>
public interface IUserEventLog
{
    /// <summary>
    /// Queues one event for <paramref name="owner"/>'s journal. Synchronous and never blocks, like
    /// every other log line (<see cref="LogFileWriter"/>).
    /// </summary>
    /// <param name="owner">Account the event belongs to.</param>
    /// <param name="author">Who wrote the event.</param>
    /// <param name="userEvent">The event.</param>
    /// <exception cref="ArgumentException">The event is malformed or too large.</exception>
    void Write(UserEventOwner owner, UserEventAuthor author, KazoUserEvent userEvent);

    /// <summary>Reads back the events of one account that pass <paramref name="filter"/>, oldest first.</summary>
    /// <param name="userId">Account whose journal is read.</param>
    /// <param name="filter">Which events to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentOutOfRangeException">The filter's limit is out of range.</exception>
    Task<IReadOnlyList<KazoUserEventEntry>> ReadAsync(
        LocalUserId userId, KazoUserEventFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the journal directory of a deleted account, archives included (ADR 0254, section 1).
    /// Does nothing when it does not exist.
    /// </summary>
    /// <param name="userId">Account that was deleted.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    Task DeleteAsync(LocalUserId userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IUserEventLog"/> on <see cref="LogFileWriter"/>: one line per event in the format of
/// every other log (ADR 0045), with the kind, subject and sender in the property block; same bounded
/// queue, batches and rotation (1 MiB, 5 archives).
/// </summary>
public sealed class FileUserEventLog : IUserEventLog
{
    /// <summary>Longest kind accepted, in characters.</summary>
    public const int MaxKindLength = 128;

    /// <summary>Longest message accepted, in characters.</summary>
    public const int MaxMessageLength = 4096;

    /// <summary>Longest subject type, id or name accepted, in characters.</summary>
    public const int MaxSubjectFieldLength = 512;

    /// <summary>Most properties one event may carry.</summary>
    public const int MaxProperties = 32;

    /// <summary>Longest property key accepted, in characters.</summary>
    public const int MaxPropertyKeyLength = 64;

    /// <summary>Longest property value accepted, in characters.</summary>
    public const int MaxPropertyValueLength = 1024;

    private readonly LogFileWriter _writer;
    private readonly ISimulationClock _clock;
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Initializes the journals on the OS's log writer.</summary>
    /// <param name="writer">Log writer of the OS.</param>
    /// <param name="clock">Clock stamping each event.</param>
    /// <param name="redactor">Redactor applied to the app's properties, like every other log line.</param>
    public FileUserEventLog(LogFileWriter writer, ISimulationClock clock, IDiagnosticRedactor? redactor = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _redactor = redactor ?? new SensitiveKeyDiagnosticRedactor();
    }

    /// <inheritdoc />
    public void Write(UserEventOwner owner, UserEventAuthor author, KazoUserEvent userEvent)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(author.AppId);
        Validate(userEvent);

        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        if (userEvent.Properties is { } extra)
        {
            foreach ((string key, string value) in extra)
            {
                properties[key] = _redactor.Redact(key, value);
            }
        }

        properties[KazoUserEventProperties.Kind] = userEvent.Kind;
        if (userEvent.Subject is { } subject)
        {
            properties[KazoUserEventProperties.SubjectType] = subject.Type;
            properties[KazoUserEventProperties.SubjectId] = subject.Id;
            properties[KazoUserEventProperties.SubjectName] = subject.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(author.SenderAppId))
        {
            properties[KazoUserEventProperties.Sender] = author.SenderAppId;
        }

        string line = LogLineFormat.Format(
            _clock.UtcNow,
            ToDiagnosticSeverity(userEvent.Severity),
            author.AppId,
            author.ProcessId,
            userEvent.Message,
            properties);
        _writer.Enqueue(LogFileTarget.ForUser(owner.UserId, owner.PrimaryGroupId), line);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KazoUserEventEntry>> ReadAsync(
        LocalUserId userId, KazoUserEventFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.EnsureValidLimit();

        IReadOnlyList<string> lines = await _writer
            .ReadLinesAsync(LogFileTarget.UserEventsPath(userId), includeArchives: true, cancellationToken)
            .ConfigureAwait(false);
        List<KazoUserEventEntry> entries = new(lines.Count);
        foreach (string line in lines)
        {
            if (KazoUserEventEntry.TryParse(line, out KazoUserEventEntry? entry))
            {
                entries.Add(entry);
            }
        }

        return filter.Apply(entries);
    }

    /// <inheritdoc />
    public Task DeleteAsync(LocalUserId userId, CancellationToken cancellationToken = default) =>
        _writer.DeleteDirectoryAsync(LogFileTarget.UserDirectory(userId), cancellationToken);

    /// <summary>Maps the three severities of a user event to the levels of the OS's logs.</summary>
    /// <param name="severity">Severity of the event.</param>
    public static DiagnosticSeverity ToDiagnosticSeverity(KazoEventSeverity severity) => severity switch
    {
        KazoEventSeverity.Warning => DiagnosticSeverity.Warning,
        KazoEventSeverity.Error => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Information
    };

    /// <summary>
    /// Refuses a malformed or oversized event before it reaches the queue: an app cannot forge the
    /// OS's own keys, nor fill the journal of the user with a few huge lines.
    /// </summary>
    /// <param name="userEvent">Event to check.</param>
    /// <exception cref="ArgumentException">The event is malformed or too large.</exception>
    public static void Validate(KazoUserEvent userEvent)
    {
        ArgumentNullException.ThrowIfNull(userEvent);
        if (string.IsNullOrWhiteSpace(userEvent.Kind)
            || userEvent.Kind.Length > MaxKindLength
            || userEvent.Kind.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"The kind of an event is one token of 1 to {MaxKindLength} characters, without whitespace.", nameof(userEvent));
        }

        if (string.IsNullOrWhiteSpace(userEvent.Message) || userEvent.Message.Length > MaxMessageLength)
        {
            throw new ArgumentException(
                $"The message of an event has 1 to {MaxMessageLength} characters.", nameof(userEvent));
        }

        if (!Enum.IsDefined(userEvent.Severity))
        {
            throw new ArgumentException("Unknown event severity.", nameof(userEvent));
        }

        if (userEvent.Subject is { } subject
            && (string.IsNullOrWhiteSpace(subject.Type)
                || string.IsNullOrWhiteSpace(subject.Id)
                || subject.DisplayName is null
                || subject.Type.Length > MaxSubjectFieldLength
                || subject.Id.Length > MaxSubjectFieldLength
                || subject.DisplayName.Length > MaxSubjectFieldLength))
        {
            throw new ArgumentException(
                $"A subject has a type and an id, and each field holds at most {MaxSubjectFieldLength} characters.", nameof(userEvent));
        }

        if (userEvent.Properties is not { } properties)
        {
            return;
        }

        if (properties.Count > MaxProperties)
        {
            throw new ArgumentException($"An event carries at most {MaxProperties} properties.", nameof(userEvent));
        }

        foreach ((string key, string value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxPropertyKeyLength || value is null || value.Length > MaxPropertyValueLength)
            {
                throw new ArgumentException(
                    $"A property key has 1 to {MaxPropertyKeyLength} characters and its value at most {MaxPropertyValueLength}.", nameof(userEvent));
            }

            if (KazoUserEventProperties.IsReserved(key))
            {
                throw new ArgumentException($"The property '{key}' is written by the OS.", nameof(userEvent));
            }
        }
    }
}
