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

    public Task<IReadOnlyList<KazoUserEventEntry>> ReadAsync(
        LocalUserId userId, KazoUserEventFilter filter, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The recording journal is write-only.");

    public Task DeleteAsync(LocalUserId userId, CancellationToken cancellationToken = default)
    {
        Deleted.Add(userId);
        return Task.CompletedTask;
    }
}
