namespace Kazo.Os.AppSdk.Apps;

/// <summary>
/// A step of an install or an update by <see cref="KazoAppPackages.InstallAsync(string, KazoPackageScope, IProgress{KazoPackageInstallProgress}?, CancellationToken)"/>
/// (ADR 0266 §7), in the order the OS runs them, like the phases a package manager prints.
/// </summary>
public enum KazoPackageInstallStage
{
    /// <summary>The OS reads the sources and the index or catalog to pick the version.</summary>
    Preparing,

    /// <summary>The OS downloads the <c>.kazoapp</c> package; <see cref="KazoPackageInstallProgress.BytesReceived"/> grows.</summary>
    Downloading,

    /// <summary>The OS checks the SHA-256, the signature, the manifest, the SDK and the block list.</summary>
    Verifying,

    /// <summary>
    /// An administrator signs in and approves in the OS's window (ADR 0189 §4). Skipped for a personal
    /// install from a trusted source.
    /// </summary>
    AwaitingApproval,

    /// <summary>The OS writes the app's files and its <c>[installed]</c> line.</summary>
    Installing
}

/// <summary>
/// Where an install stands, reported to the <see cref="IProgress{T}"/> given to
/// <see cref="KazoAppPackages.InstallAsync(string, KazoPackageScope, IProgress{KazoPackageInstallProgress}?, CancellationToken)"/>
/// (ADR 0266 §7). Every stage is reported once when it starts; <see cref="KazoPackageInstallStage.Downloading"/>
/// is reported again as the bytes arrive. The returned task completes after the last one.
/// </summary>
/// <param name="Stage">The current step.</param>
/// <param name="BytesReceived">Bytes of the package downloaded so far; <see langword="null"/> outside the download.</param>
/// <param name="TotalBytes">
/// Size of the package when the host announced it; <see langword="null"/> when it is unknown (the
/// progress is then indeterminate) or outside the download.
/// </param>
public sealed record KazoPackageInstallProgress(KazoPackageInstallStage Stage, long? BytesReceived = null, long? TotalBytes = null)
{
    /// <summary>
    /// Gets the downloaded share, from 0 to 1, or <see langword="null"/> when the size is unknown or
    /// the stage is not the download.
    /// </summary>
    public double? Fraction => Stage == KazoPackageInstallStage.Downloading && BytesReceived is { } received && TotalBytes is > 0 and { } total
        ? Math.Clamp((double)received / total, 0, 1)
        : null;
}
