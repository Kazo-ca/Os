using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.UserEvents;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppUserEventsGateway"/> (ADR 0254): one app instance's access to the journal
/// of the user it runs for, checking <c>events.write</c> on every call. The OS fills in the app, the
/// process and the sender from the instance's own context, so an app can neither write to another
/// user's journal nor sign a line with another app's name, and it reads back only its own lines.
/// </summary>
internal sealed class AppUserEventsGateway : IAppUserEventsGateway
{
    private readonly IUserEventLog _log;
    private readonly ICapabilityChecker _capabilities;
    private readonly UserEventOwner _owner;
    private readonly string _appId;
    private readonly long? _processId;
    private readonly Func<string?> _sender;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="log">The OS's user journals.</param>
    /// <param name="capabilities">Capability checker bound to the app and its user.</param>
    /// <param name="owner">The user the app runs for, the only journal it reaches.</param>
    /// <param name="appId">The app, taken from its manifest.</param>
    /// <param name="processId">The app's process.</param>
    /// <param name="sender">
    /// Reads the app that launched this instance or opened a file with it, when there is one; read at
    /// each write because the launch sets it after the context is built.
    /// </param>
    public AppUserEventsGateway(
        IUserEventLog log,
        ICapabilityChecker capabilities,
        UserEventOwner owner,
        string appId,
        long? processId,
        Func<string?> sender)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _appId = appId;
        _processId = processId;
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    /// <inheritdoc />
    public void Write(KazoUserEvent userEvent)
    {
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        _capabilities.Require(AppCapabilities.EventsWrite);
        _log.Write(_owner, new UserEventAuthor(_appId, _processId, _sender()), userEvent);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoUserEventEntry>> ReadAsync(
        KazoUserEventFilter filter, CancellationToken cancellationToken = default)
    {
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.EventsWrite);

        // The OS narrows the read to this app, whatever the filter asked: an app sees only its own lines.
        return await _log.ReadAsync(_owner.UserId, filter with { AppId = _appId }, cancellationToken).ConfigureAwait(false);
    }
}
