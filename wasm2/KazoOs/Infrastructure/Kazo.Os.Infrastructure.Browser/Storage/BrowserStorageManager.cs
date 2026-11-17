using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Storage;

/// <summary>Reports browser storage capacity and requests durable storage retention.</summary>
/// <remarks>
/// It is also the OS's <see cref="IStorageCapacityProbe"/> (ADR 0099, section 2): the low-space
/// warning, the root reserve and <c>df</c> read the origin's real usage and quota through it; and its
/// <see cref="IOriginStorage"/> (ADR 0250): <c>persist()</c> once for the origin, the boot marker of
/// the eviction check, and whether a database or a file of the Origin Private File System exists.
/// </remarks>
public sealed class BrowserStorageManager : IStorageCapacityProbe, IOriginStorage, IAsyncDisposable
{
    /// <summary>Absolute free-space threshold used alongside the proportional threshold.</summary>
    public const long LowSpaceByteThreshold = StorageSpacePolicy.LowSpaceBytes;

    /// <summary>Proportional free-space threshold used alongside the absolute threshold.</summary>
    public const double LowSpaceRatioThreshold = StorageSpacePolicy.LowSpaceRatio;

    private const string ModulePath = "./_content/Kazo.Os.Infrastructure.Browser/storageManager.js";
    private readonly IJSRuntime _runtime;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;
    private bool _disposed;

    /// <summary>Creates a browser storage manager backed by the native StorageManager API.</summary>
    public BrowserStorageManager(IJSRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>Reads the current storage estimate and durable-retention state.</summary>
    public async ValueTask<BrowserStorageStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        BrowserStorageEstimate estimate = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeAsync<BrowserStorageEstimate>("getStorageEstimate", cancellationToken)
            .ConfigureAwait(false);
        StorageCapacity capacity = new(estimate.UsageBytes, estimate.QuotaBytes);
        long availableBytes = capacity.AvailableBytes;
        bool isLowSpace = StorageSpacePolicy.IsLow(capacity);

        return new BrowserStorageStatus(
            estimate.UsageBytes,
            estimate.QuotaBytes,
            availableBytes,
            estimate.IsPersisted,
            isLowSpace);
    }

    /// <inheritdoc />
    public async ValueTask<StorageCapacity?> GetCapacityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            BrowserStorageStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return status.QuotaBytes > 0 ? new StorageCapacity(status.UsageBytes, status.QuotaBytes) : null;
        }
        catch (JSException)
        {
            // A browser without the StorageManager API cannot tell its quota.
            return null;
        }
    }

    /// <summary>Requests durable retention and returns whether the browser granted it.</summary>
    public async ValueTask<bool> RequestPersistenceAsync(
        CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeAsync<bool>("requestPersistence", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    async ValueTask<OriginStorageStatus?> IOriginStorage.GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            BrowserStorageEstimate estimate = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<BrowserStorageEstimate>("getStorageEstimate", cancellationToken)
                .ConfigureAwait(false);
            return new OriginStorageStatus(estimate.UsageBytes, estimate.QuotaBytes, estimate.IsPersisted)
            {
                IndexedDbBytes = estimate.IndexedDbBytes,
                CacheBytes = estimate.CacheBytes,
                FileSystemBytes = estimate.FileSystemBytes,
            };
        }
        catch (JSException)
        {
            // A browser without the StorageManager API cannot tell its quota.
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> ReadBootMarkerAsync(CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeAsync<string?>("readBootMarker", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask WriteBootMarkerAsync(string marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeVoidAsync("writeBootMarker", cancellationToken, marker)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool?> DatabaseExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeAsync<bool?>("databaseExists", cancellationToken, name)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool?> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
            .InvokeAsync<bool?>("fileExists", cancellationToken, path)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Browser shutdown can disconnect JS before the DI scope is disposed.
            }
        }

        _moduleGate.Dispose();
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _module ??= await _runtime.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, ModulePath).ConfigureAwait(false);
            return _module;
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    private sealed record BrowserStorageEstimate(
        long UsageBytes, long QuotaBytes, bool IsPersisted, long? IndexedDbBytes = null, long? CacheBytes = null, long? FileSystemBytes = null);
}

/// <summary>Describes the browser's current origin-storage state.</summary>
/// <param name="UsageBytes">Bytes currently used by the origin.</param>
/// <param name="QuotaBytes">Bytes currently granted to the origin.</param>
/// <param name="AvailableBytes">Non-negative difference between quota and usage.</param>
/// <param name="IsPersisted">Whether durable retention is currently granted.</param>
/// <param name="IsLowSpace">Whether free space is below 10 percent or 64 MiB.</param>
public sealed record BrowserStorageStatus(
    long UsageBytes,
    long QuotaBytes,
    long AvailableBytes,
    bool IsPersisted,
    bool IsLowSpace);

/// <summary>Represents a recoverable browser quota exhaustion failure.</summary>
public sealed class BrowserStorageQuotaException : Exception
{
    /// <summary>Creates a quota failure while preserving the browser exception.</summary>
    public BrowserStorageQuotaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}