using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;
using Kazo.Os.Windowing.Abstractions;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Tells the user why a window did not change screen (ADR 0263): « “Kazo Word” reste sur son
/// écran — Pas encore disponible pour cette app : problème connu, une solution est en cours ». It
/// listens to <see cref="WindowKeyboardCommands.ScreenChangeRefused"/>, raised by « Autre écran »,
/// the screen shortcuts and a drop on another screen.
/// </summary>
internal sealed class ScreenChangeRefusedNotifier : IDisposable
{
    /// <summary>Source of the notification: the window manager, which no app owns.</summary>
    public const string SourceAppId = "org.kazoos.windows";

    private readonly WindowKeyboardCommands _commands;
    private readonly INotificationQueue _queue;
    private readonly ISimulationClock _clock;
    private readonly Func<LocalUserId?> _currentUser;
    private readonly Func<WindowId, string?> _appName;
    private readonly IStringLocalizer _text;

    /// <summary>Creates the notifier and starts listening.</summary>
    /// <param name="commands">The window manager's commands, which raise the refusal.</param>
    /// <param name="queue">The OS notification queue.</param>
    /// <param name="clock">The simulation clock: the notification's time.</param>
    /// <param name="currentUser">The session user, or <see langword="null"/> when nobody is signed in.</param>
    /// <param name="appName">The name shown for a window (its app's localized name), or <see langword="null"/> when it is gone.</param>
    /// <param name="text">The platform's texts.</param>
    public ScreenChangeRefusedNotifier(
        WindowKeyboardCommands commands,
        INotificationQueue queue,
        ISimulationClock clock,
        Func<LocalUserId?> currentUser,
        Func<WindowId, string?> appName,
        IStringLocalizer text)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _appName = appName ?? throw new ArgumentNullException(nameof(appName));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _commands.ScreenChangeRefused += OnRefused;
    }

    /// <inheritdoc />
    public void Dispose() => _commands.ScreenChangeRefused -= OnRefused;

    private void OnRefused(WindowId windowId)
    {
        if (_currentUser() is not { } user || _appName(windowId) is not { Length: > 0 } name)
        {
            return;
        }

        _queue.Enqueue(new Notification(
            NotificationId.FromGuid(Guid.NewGuid()),
            user,
            SourceAppId,
            NotificationSeverity.Information,
            _text["WindowMenu.ScreenChangeRefused.Title", name],
            _text["WindowMenu.ScreenChangeUnavailable"],
            [],
            _clock.UtcNow,
            expiresAtUtc: null));
    }
}
