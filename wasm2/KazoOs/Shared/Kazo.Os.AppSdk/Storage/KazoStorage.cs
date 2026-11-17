namespace Kazo.Os.AppSdk.Storage;

/// <summary>
/// The browser storage of Kazo OS (ADR 0250): how much the origin uses and may use, whether the
/// browser protects it from eviction, and, for a feature that keeps data in the Origin Private File
/// System (a virtual machine's disks, for example), a marker the OS watches and the space it declares.
/// </summary>
/// <remarks>
/// <para>
/// The quota is the origin's: Kazo OS's files, its caches and every feature's disks share it, and the
/// reserve of ADR 0099 counts all of them. A feature never calls <c>navigator.storage</c> itself: the
/// OS asks once for the whole origin.
/// </para>
/// <example>
/// <code>
/// // In a feature, after the user's first « x86 vm create »:
/// await KazoStorage.RequestPersistenceAsync();
/// await KazoStorage.RegisterMarkerAsync("osx86/.kazoos-marker");
/// await KazoStorage.ReportUsageAsync(disksBytes);
/// KazoStorage.Changed += (_, change) =>
/// {
///     if (change.Kind == KazoStorageChangeKind.Evicted) { /* offer to export what is left */ }
/// };
/// </code>
/// </example>
/// </remarks>
public static class KazoStorage
{
    /// <summary>Gets whether the host has browser storage to report on.</summary>
    public static bool IsSupported => KazoExecutionContext.Current.Storage.IsSupported;

    /// <summary>
    /// Reads the origin's usage and quota, whether it is protected and whether its free space is low
    /// (ADR 0099), with the space the calling feature declared. No permission: <c>df</c> shows the same.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The estimate; <see cref="KazoStorageEstimate.Unknown"/> on a host without browser storage.</returns>
    public static ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken cancellationToken = default) =>
        KazoExecutionContext.Current.Storage.GetEstimateAsync(cancellationToken);

    /// <summary>
    /// Asks the browser to protect the origin from eviction (<c>navigator.storage.persist()</c>). The OS
    /// asks once per origin and not at all when the origin is protected already. Call it right after a
    /// gesture of the user (a click, a command typed): without one the browser refuses and this returns
    /// <see langword="false"/>, with no error.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the origin is protected.</returns>
    /// <exception cref="IO.KazoUnauthorizedAccessException">The caller is neither a feature nor a system app (<c>manifest.feature.only</c>).</exception>
    public static ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default) =>
        KazoExecutionContext.Current.Storage.RequestPersistenceAsync(cancellationToken);

    /// <summary>
    /// Records a marker file the calling feature wrote in the Origin Private File System. At each boot
    /// the OS checks that it still exists; when the browser erased it, the feature receives
    /// <see cref="Changed"/> (<see cref="KazoStorageChangeKind.Evicted"/>) and the user a notification.
    /// </summary>
    /// <param name="path">Its path from the root of the Origin Private File System, such as <c>osx86/.kazoos-marker</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">The path is empty, absolute or leaves the root.</exception>
    /// <exception cref="IO.KazoUnauthorizedAccessException">The caller is not a feature.</exception>
    public static ValueTask RegisterMarkerAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return KazoExecutionContext.Current.Storage.RegisterMarkerAsync(path, cancellationToken);
    }

    /// <summary>
    /// Declares the bytes the calling feature keeps in the Origin Private File System, which the OS
    /// cannot measure cheaply: Settings ▸ Storage and <c>df</c> show them (<c>/dev/feature/&lt;id&gt;</c>).
    /// </summary>
    /// <param name="bytes">The bytes, zero or more.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    /// <exception cref="IO.KazoUnauthorizedAccessException">The caller is not a feature.</exception>
    public static ValueTask ReportUsageAsync(long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return KazoExecutionContext.Current.Storage.ReportUsageAsync(bytes, cancellationToken);
    }

    /// <summary>Lists the features that registered a marker or declared their usage, for Settings ▸ Storage and <c>df</c>.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static ValueTask<IReadOnlyList<KazoFeatureStorageUsage>> GetFeatureUsageAsync(CancellationToken cancellationToken = default) =>
        KazoExecutionContext.Current.Storage.GetFeatureUsageAsync(cancellationToken);

    /// <summary>
    /// Raised to the calling app when the browser erased its storage or withdrew its protection
    /// (ADR 0250 §2). A change the OS found at boot is delivered as soon as the handler is added, so a
    /// feature learns it at its next launch.
    /// </summary>
    public static event EventHandler<KazoStorageChangedEventArgs>? Changed
    {
        add => KazoExecutionContext.Current.Storage.Changed += value;
        remove => KazoExecutionContext.Current.Storage.Changed -= value;
    }
}
