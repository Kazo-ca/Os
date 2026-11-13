namespace Kazo.Os.Platform.Core.AppLinks;

/// <summary>
/// Runs a direct app launch link in a guest session (ADR 0246 §3): finds the app in the official
/// signed catalog only, installs it for the guest, launches it, and reports failures as an OS
/// notification. The host calls it once the guest's desktop is shown.
/// </summary>
/// <remarks>
/// Work item 2 of the <c>app-link</c> series provides the implementation. While none is
/// registered, the host opens the Store on the app's page instead, as for an account (§4).
/// </remarks>
public interface IAppLaunchLinkRunner
{
    /// <summary>Installs (if needed) and launches the app the link names, in the current guest session.</summary>
    /// <param name="link">The link read at boot.</param>
    /// <param name="cancellationToken">Cancels the search, the download or the install.</param>
    Task RunAsync(AppLaunchLink link, CancellationToken cancellationToken = default);
}
