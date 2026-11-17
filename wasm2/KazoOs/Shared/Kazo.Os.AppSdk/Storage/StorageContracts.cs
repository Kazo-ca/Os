namespace Kazo.Os.AppSdk.Storage;

/// <summary>
/// The browser storage of the page as the OS reads it (ADR 0250 §1): one quota for the whole origin,
/// shared by Kazo OS's own data (IndexedDB), its caches and the features' Origin Private File System.
/// </summary>
/// <param name="UsageBytes">Bytes the origin uses, as <c>navigator.storage.estimate()</c> says.</param>
/// <param name="QuotaBytes">Bytes the browser grants the origin; 0 when it does not say.</param>
/// <param name="IsPersisted">Whether the browser protects the origin from eviction (<c>navigator.storage.persisted()</c>).</param>
/// <param name="IsLowSpace">Whether the free space is under the thresholds of ADR 0099.</param>
public sealed record KazoStorageEstimate(long UsageBytes, long QuotaBytes, bool IsPersisted, bool IsLowSpace)
{
    /// <summary>Gets whether the OS could read the estimate; false on a host without browser storage.</summary>
    public bool IsKnown { get; init; } = true;

    /// <summary>Gets the bytes of the origin's IndexedDB databases (Kazo OS's files and settings), when the browser details them.</summary>
    public long? IndexedDbBytes { get; init; }

    /// <summary>Gets the bytes of the origin's Cache Storage (the OS's own files and those of installed apps), when the browser details them.</summary>
    public long? CacheBytes { get; init; }

    /// <summary>Gets the bytes of the origin's Origin Private File System (the features' disks), when the browser details them.</summary>
    public long? FileSystemBytes { get; init; }

    /// <summary>
    /// Gets the bytes the calling feature last declared with <see cref="KazoStorage.ReportUsageAsync(long, CancellationToken)"/>,
    /// or <see langword="null"/> when it declared none or the caller is not a feature (ADR 0250 §3).
    /// </summary>
    public long? DeclaredUsageBytes { get; init; }

    /// <summary>
    /// Gets whether the browser withdrew the protection it had granted (<see cref="IsPersisted"/> false
    /// after it was true): Settings ▸ Storage warns, no screen (ADR 0250 §2).
    /// </summary>
    public bool PersistenceLost { get; init; }

    /// <summary>An estimate the host could not read.</summary>
    public static KazoStorageEstimate Unknown { get; } = new(0, 0, false, false) { IsKnown = false };
}

/// <summary>What happened to the browser storage of a feature (ADR 0250 §2).</summary>
public enum KazoStorageChangeKind
{
    /// <summary>The browser erased the feature's storage: its marker file is gone from the Origin Private File System.</summary>
    Evicted = 0,

    /// <summary>The browser withdrew the protection against eviction it had granted.</summary>
    PersistenceLost = 1,
}

/// <summary>Arguments of <see cref="KazoStorage.Changed"/>.</summary>
public sealed class KazoStorageChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="featureId">The feature whose storage changed.</param>
    /// <param name="markerPath">The marker the feature registered, when its storage was evicted.</param>
    /// <param name="detectedAtUtc">When the OS noticed it.</param>
    public KazoStorageChangedEventArgs(KazoStorageChangeKind kind, string featureId, string? markerPath, DateTimeOffset detectedAtUtc)
    {
        Kind = kind;
        FeatureId = featureId;
        MarkerPath = markerPath;
        DetectedAtUtc = detectedAtUtc;
    }

    /// <summary>Gets what happened.</summary>
    public KazoStorageChangeKind Kind { get; }

    /// <summary>Gets the feature whose storage changed.</summary>
    public string FeatureId { get; }

    /// <summary>Gets the marker the feature registered, when its storage was evicted.</summary>
    public string? MarkerPath { get; }

    /// <summary>Gets when the OS noticed it.</summary>
    public DateTimeOffset DetectedAtUtc { get; }
}

/// <summary>
/// The space a feature declared for its own storage (ADR 0250 §3), as Settings ▸ Storage and <c>df</c>
/// show it.
/// </summary>
/// <param name="FeatureId">The feature.</param>
/// <param name="Name">Its name.</param>
/// <param name="UsageBytes">The bytes it declared, or <see langword="null"/> when it registered a marker but declared no usage.</param>
/// <param name="ReportedAtUtc">When it declared them, or <see langword="null"/>.</param>
public sealed record KazoFeatureStorageUsage(string FeatureId, string Name, long? UsageBytes, DateTimeOffset? ReportedAtUtc);

/// <summary>
/// The OS side of <see cref="KazoStorage"/> (ADR 0250), one per app instance. Reading needs no
/// capability; asking for persistence is for the features and the system apps, registering a marker
/// and declaring usage for the features.
/// </summary>
public interface IAppStorageGateway
{
    /// <summary>Gets whether the host has browser storage to report on.</summary>
    bool IsSupported { get; }

    /// <summary>Reads the estimate of the origin, with the caller's declared usage.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the browser to protect the origin, once per origin.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a marker file of the calling feature's Origin Private File System.</summary>
    /// <param name="path">Its path from the root of the Origin Private File System.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RegisterMarkerAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Records the bytes the calling feature keeps in its Origin Private File System.</summary>
    /// <param name="bytes">The bytes, zero or more.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask ReportUsageAsync(long bytes, CancellationToken cancellationToken = default);

    /// <summary>Lists the features that registered a marker or declared their usage.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<KazoFeatureStorageUsage>> GetFeatureUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when the browser erased the caller's storage, or withdrew its protection. A change the OS
    /// found before the handler was added is delivered as soon as it is added (ADR 0250 §2: « au
    /// prochain lancement »).
    /// </summary>
    event EventHandler<KazoStorageChangedEventArgs>? Changed;
}

/// <summary>The gateway of a host without browser storage: nothing to report, nothing to protect.</summary>
public sealed class UnsupportedAppStorageGateway : IAppStorageGateway
{
    private UnsupportedAppStorageGateway()
    {
    }

    /// <summary>Gets the single instance.</summary>
    public static UnsupportedAppStorageGateway Instance { get; } = new();

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(KazoStorageEstimate.Unknown);

    /// <inheritdoc />
    public ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask RegisterMarkerAsync(string path, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask ReportUsageAsync(long bytes, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoFeatureStorageUsage>> GetFeatureUsageAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoFeatureStorageUsage>>([]);

    /// <inheritdoc />
    public event EventHandler<KazoStorageChangedEventArgs>? Changed
    {
        add
        {
        }

        remove
        {
        }
    }
}
