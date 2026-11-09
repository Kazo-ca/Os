using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Lifecycle;

/// <summary>
/// Decides, before an app the user opens is started, whether the OS may start it now (ADR 0239).
/// The browser host refuses while the origin serves a newer Kazo OS than the running one: an app
/// started from an outdated copy could load files of another version, so the user restarts onto
/// the new version first, as a real OS asks to restart after an update before opening programs.
/// </summary>
/// <remarks>
/// <see cref="AppLifecycleOrchestrator"/> asks the gate it finds in its services, for interactive
/// launches of <see cref="AppKind.Window"/> and <see cref="AppKind.Web"/> apps only: services and
/// terminal commands are part of the running system and keep working. An app already running is
/// focused without asking.
/// </remarks>
public interface IAppLaunchGate
{
    /// <summary>Checks whether <paramref name="manifest"/> may start now.</summary>
    /// <param name="manifest">The app the user opens.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="null"/> to start the app; otherwise why it does not start.</returns>
    ValueTask<AppLaunchRefusal?> CheckAsync(AppManifest manifest, CancellationToken cancellationToken);
}

/// <summary>Why <see cref="IAppLaunchGate"/> refused a launch (ADR 0239).</summary>
/// <param name="ErrorCode">Stable code, for example <see cref="AppLaunchRefusal.UpdateRequiredCode"/>.</param>
/// <param name="Message">The text the shell shows, in the session language.</param>
public sealed record AppLaunchRefusal(string ErrorCode, string Message)
{
    /// <summary>The code of a launch refused because a newer Kazo OS waits for a restart.</summary>
    public const string UpdateRequiredCode = "lifecycle.os.update-required";
}
