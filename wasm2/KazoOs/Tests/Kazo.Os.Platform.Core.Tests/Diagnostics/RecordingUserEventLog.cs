using Kazo.Os.AppSdk.UserEvents;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

/// <summary>
/// An <see cref="IUserEventLog"/> that keeps what the OS writes in memory, so a test checks the
/// events of its own personal actions (ADR 0254, section 2) without a filesystem. Each write is
/// validated like the real journal does.
/// </summary>
internal sealed class RecordingUserEventLog : IUserEventLog
{
    /// <summary>Gets every event written, in order.</summary>
    public List<(UserEventOwner Owner, UserEventAuthor Author, KazoUserEvent Event)> Written { get; } = [];

    /// <summary>Gets every account whose journal was deleted.</summary>
    public List<LocalUserId> Deleted { get; } = [];

    public void Write(UserEventOwner owner, UserEventAuthor author, KazoUserEvent userEvent)
    {
        FileUserEventLog.Validate(userEvent);
        Written.Add((owner, author, userEvent));
    }

    /// <summary>Gets the events each account's journal returns when read.</summary>
    public Dictionary<LocalUserId, List<KazoUserEventEntry>> Journals { get; } = [];

    /// <summary>Gets every account whose journal was read, in order.</summary>
    public List<LocalUserId> Reads { get; } = [];

    public Task<IReadOnlyList<KazoUserEventEntry>> ReadAsync(
        LocalUserId userId, KazoUserEventFilter filter, CancellationToken cancellationToken = default)
    {
        Reads.Add(userId);
        return Task.FromResult(filter.Apply(Journals.GetValueOrDefault(userId) ?? []));
    }

    /// <summary>Builds an event of <paramref name="appId"/> as read back from a journal.</summary>
    public static KazoUserEventEntry Entry(string appId, string message) => new(
        DateTimeOffset.UnixEpoch, appId, null, "test.event", message, null, KazoEventSeverity.Info,
        new Dictionary<string, string>(), null);

    public Task DeleteAsync(LocalUserId userId, CancellationToken cancellationToken = default)
    {
        Deleted.Add(userId);
        return Task.CompletedTask;
    }
}
