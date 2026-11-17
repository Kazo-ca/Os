namespace Kazo.Os.Simulation.Abstractions.Metrics;

/// <summary>
/// What the browser says about the storage of the page's origin (ADR 0250): <c>navigator.storage.estimate()</c>
/// and <c>persisted()</c>, with the detail per kind of storage when the browser gives it (Chromium's
/// <c>usageDetails</c>).
/// </summary>
/// <param name="UsageBytes">Bytes the origin uses.</param>
/// <param name="QuotaBytes">Bytes the browser grants it; 0 when it does not say.</param>
/// <param name="IsPersisted">Whether the browser protects it from eviction.</param>
public sealed record OriginStorageStatus(long UsageBytes, long QuotaBytes, bool IsPersisted)
{
    /// <summary>Gets the bytes of its IndexedDB databases, when the browser details them.</summary>
    public long? IndexedDbBytes { get; init; }

    /// <summary>Gets the bytes of its Cache Storage, when the browser details them.</summary>
    public long? CacheBytes { get; init; }

    /// <summary>Gets the bytes of its Origin Private File System, when the browser details them.</summary>
    public long? FileSystemBytes { get; init; }
}

/// <summary>
/// The browser storage of the page's origin, as the OS needs it for ADR 0250: the estimate, the one
/// <c>navigator.storage.persist()</c> of the origin, the boot marker kept in <c>localStorage</c>, and
/// whether a database or a file of the Origin Private File System still exists. The browser host
/// implements it; a host without browser storage registers none.
/// </summary>
public interface IOriginStorage
{
    /// <summary>Reads the estimate, or <see langword="null"/> when the browser cannot give one.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<OriginStorageStatus?> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Calls <c>navigator.storage.persist()</c>; the browser grants it only after a gesture of the user.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the origin is protected.</returns>
    ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the boot marker the OS keeps in <c>localStorage</c>, or <see langword="null"/>.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<string?> ReadBootMarkerAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the boot marker in <c>localStorage</c>.</summary>
    /// <param name="marker">Its content.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteBootMarkerAsync(string marker, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether an IndexedDB database of the origin exists, without opening it (an open would
    /// create it); <see langword="null"/> when the browser cannot say.
    /// </summary>
    /// <param name="name">The database's name.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<bool?> DatabaseExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Gets whether a file of the Origin Private File System exists; <see langword="null"/> when the browser has none.</summary>
    /// <param name="path">Its path from the root, with <c>/</c> between the folders.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<bool?> FileExistsAsync(string path, CancellationToken cancellationToken = default);
}
