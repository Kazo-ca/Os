namespace Kazo.Os.Platform.Blazor.LazyLoading;

/// <summary>
/// ADR 0239 §1: when Kazo OS starts, it makes sure it runs the version its origin serves, as a
/// machine applies a pending update while it boots. The host reads <c>version.json</c> once before
/// the first screen; when it is newer, the host shows « Mise à jour », downloads the new version
/// and restarts onto it (ADR 0223) instead of opening the sign-in screen.
/// </summary>
/// <remarks>
/// An origin that does not answer in time (offline, server down) is not an update: the OS starts
/// as it is. A restart that did not land on the new version (a deployment still in progress, a
/// worker that would not install) must not loop: <c>claimAttempt</c> answers <see langword="false"/>
/// when this page already tried to update to the same version (the browser host keeps the target
/// in <c>sessionStorage</c>), and the OS then starts with the « update ready » notice as before.
/// </remarks>
public sealed class HostBootUpdateCheck
{
    /// <summary>How long the boot waits for <c>version.json</c> before starting anyway.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HostReleaseMonitor _monitor;
    private readonly Func<string, ValueTask<bool>> _claimAttempt;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the check.</summary>
    /// <param name="monitor">Reads the served version.</param>
    /// <param name="claimAttempt">
    /// Records an attempt to update to a target (<c>3.3.0+812</c>) and answers whether it is the
    /// first one for that target in this page's tab.
    /// </param>
    /// <param name="timeout">How long to wait for the origin; <see cref="DefaultTimeout"/> when <see langword="null"/>.</param>
    public HostBootUpdateCheck(HostReleaseMonitor monitor, Func<string, ValueTask<bool>> claimAttempt, TimeSpan? timeout = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _claimAttempt = claimAttempt ?? throw new ArgumentNullException(nameof(claimAttempt));
        _timeout = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeout, TimeSpan.Zero);
    }

    /// <summary>Checks the served version once and tells whether the boot must update first.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when a newer version is served and this tab did not try it yet.</returns>
    public async Task<bool> ShouldUpdateAsync(CancellationToken cancellationToken = default)
    {
        bool outdated;
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_timeout);
            try
            {
                outdated = await _monitor.CheckAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The origin is slow: start the running version, the five-minute check goes on.
                return false;
            }
        }

        return outdated && await _claimAttempt(Target).ConfigureAwait(false);
    }

    // The version and build the restart should land on; "files" when only the files changed (ADR 0162).
    private string Target =>
        $"{_monitor.AvailableVersion ?? _monitor.RunningVersion}+{_monitor.AvailableBuild?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "files"}";
}
