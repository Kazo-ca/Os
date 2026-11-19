using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.UserEvents;

/// <summary>
/// The journal of the user the app runs for (ADR 0254): what happened to their machines, programs and
/// files, kept in <c>/var/log/users/&lt;id&gt;/events.log</c>, readable by that user and, after the
/// OS's authentication, by an administrator. Unlike the app's log (<c>AppContext.Logging</c>,
/// readable by everyone), an event here may name something personal.
/// </summary>
/// <remarks>
/// Both members need the normal capability <c>events.write</c>; without it they raise
/// <see cref="KazoUnauthorizedAccessException"/>. The OS stamps each line with the time, the app (from
/// its own context, never from the caller), the process and the sender when it knows one. Every
/// member targets the app running the current code (ambient context of ADR 0043); a Blazor component
/// outside that scope calls <c>AppContext.UserEvents</c> directly. This is not the bus of
/// <c>AppContext.Events</c>.
/// </remarks>
public static class KazoEvents
{
    /// <summary>
    /// Writes <paramref name="userEvent"/> to the journal of the user the app runs for. Returns at
    /// once: the line reaches the file with the OS's next batch, within a second.
    /// </summary>
    /// <param name="userEvent">Event to write.</param>
    /// <exception cref="ArgumentException">
    /// The kind is empty or holds whitespace, the message is empty, a property uses a key of
    /// <see cref="KazoUserEventProperties"/>, or the event is too large.
    /// </exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>events.write</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static void Write(KazoUserEvent userEvent)
    {
        ArgumentNullException.ThrowIfNull(userEvent);
        try
        {
            Gateway.Write(userEvent);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>
    /// Reads back this app's own events from the journal of the user it runs for (a program's
    /// history, the past decisions about a machine). The OS returns only the lines this app wrote.
    /// </summary>
    /// <param name="filter">Which events to return; <see langword="null"/> for the newest 1000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events, oldest first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The filter's limit is out of range.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>events.write</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<KazoUserEventEntry>> ReadAsync(
        KazoUserEventFilter? filter = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Gateway.ReadAsync(filter ?? new KazoUserEventFilter(), cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }

    private static IAppUserEventsGateway Gateway => KazoExecutionContext.Current.UserEvents;
}
