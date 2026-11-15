using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// Runs bounded browser-filesystem maintenance: collects the orphan file content (chunks no file
/// points to any more) older than the retention window of <see cref="FileContentStoragePolicy"/>.
/// </summary>
/// <remarks>
/// Like a filesystem freeing the blocks of deleted files, but deferred: a rewritten or deleted file
/// leaves its old content in <c>fsContent</c>, and this pass removes it later. The OS storage
/// monitor runs <see cref="ReclaimAsync"/> in the background at boot and again when storage gets
/// low (Os#537).
/// </remarks>
public sealed class IndexedDbFileSystemMaintenance : IStorageReclaimer, IAsyncDisposable
{
    /// <summary>Gets how many content hashes one pass deletes at most (one IndexedDB transaction).</summary>
    public const int HashesPerPass = 64;

    /// <summary>
    /// Gets how many passes one <see cref="ReclaimAsync"/> runs at most, so a store with a huge
    /// backlog is emptied over several calls instead of holding the database for long.
    /// </summary>
    public const int MaxPassesPerReclaim = 32;

    private readonly IndexedDbFileContentRepository _contentRepository;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes maintenance with the accepted content retention policy.</summary>
    public IndexedDbFileSystemMaintenance(IJSRuntime runtime)
        : this(runtime, FileContentStoragePolicy.Default, TimeProvider.System)
    {
    }

    internal IndexedDbFileSystemMaintenance(
        IJSRuntime runtime,
        FileContentStoragePolicy policy,
        TimeProvider timeProvider)
    {
        _contentRepository = new IndexedDbFileContentRepository(runtime, policy, timeProvider);
    }

    /// <summary>Runs the same bounded collection pass intended for deterministic host startup.</summary>
    public ValueTask<IndexedDbFileContentCleanupResult> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        _contentRepository.CleanupAsync(cancellationToken: cancellationToken);

    /// <summary>Runs a bounded collection pass for explicit recovery or quota maintenance.</summary>
    public ValueTask<IndexedDbFileContentCleanupResult> CleanupAsync(
        int maximumHashes = 64,
        CancellationToken cancellationToken = default) =>
        _contentRepository.CleanupAsync(maximumHashes, lowSpace: false, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Runs passes of <see cref="HashesPerPass"/> hashes until one finds fewer candidates than that,
    /// deletes nothing, or <see cref="MaxPassesPerReclaim"/> passes ran. A call made while another
    /// one runs returns 0 at once: the running one already does the work.
    /// </remarks>
    public async ValueTask<long> ReclaimAsync(bool lowSpace, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        try
        {
            long deleted = 0;
            for (int pass = 0; pass < MaxPassesPerReclaim; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedDbFileContentCleanupResult result = await _contentRepository
                    .CleanupAsync(HashesPerPass, lowSpace, cancellationToken)
                    .ConfigureAwait(false);
                deleted += result.DeletedChunks;
                if (result.CandidateHashes < HashesPerPass || result.DeletedChunks == 0)
                {
                    break;
                }
            }

            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _contentRepository.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
