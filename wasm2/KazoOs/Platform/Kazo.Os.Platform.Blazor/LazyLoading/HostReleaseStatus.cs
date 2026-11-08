namespace Kazo.Os.Platform.Blazor.LazyLoading;

/// <summary>
/// Whether the running OS is the one its origin serves now (ADR 0236), as the sign-in screen shows
/// it next to the version.
/// </summary>
public enum HostReleaseStatus
{
    /// <summary>No check has finished yet: the screen says it is checking.</summary>
    NotChecked = 0,

    /// <summary>The origin serves the running version and build.</summary>
    UpToDate,

    /// <summary>
    /// The origin serves another version or another build, or the files changed under the page
    /// (ADR 0162). It stays so until the restart: the running files do not change.
    /// </summary>
    UpdateAvailable,

    /// <summary>The origin did not answer or its <c>version.json</c> is unreadable (offline, server down).</summary>
    Unreachable,
}
