using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>Executes atomic IndexedDB filesystem metadata mutation plans.</summary>
internal sealed class IndexedDbFileSystemWriter : IAsyncDisposable
{
    private const string Boundary = "FileSystemMetadataMutation";
    private const string PublishBoundary = "FileSystemContentPublish";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes the browser filesystem metadata writer.</summary>
    /// <param name="runtime">JavaScript runtime.</param>
    /// <param name="disk">Optional statistics of <c>hda</c>; each metadata transaction counts as one 4 KiB write.</param>
    internal IndexedDbFileSystemWriter(IJSRuntime runtime, IBlockDeviceStatistics? disk = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime, disk);
    }

    /// <summary>Atomically creates an entry and its parent link.</summary>
    internal async ValueTask CreateAsync(
        IndexedDbFileSystemEntryRecord parent,
        FileSystemEntryMetadata created,
        FileSystemEntryName name,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanCreate(parent, created, name, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically replaces one entry's permission bits and metadata revision.</summary>
    internal async ValueTask SetPermissionsAsync(
        IndexedDbFileSystemEntryRecord entry,
        FileSystemPermissions permissions,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanSetPermissions(entry, permissions, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically publishes file metadata that references previously persisted chunks, after
    /// checking that all <paramref name="chunkCount"/> chunks are present (ADR 0273 section 3).
    /// </summary>
    /// <param name="entry">The file entry as read; its revision is asserted.</param>
    /// <param name="contentHash">Hash of the content already written.</param>
    /// <param name="length">Content length in bytes.</param>
    /// <param name="chunkCount">Chunks the content must have in <c>fsContent</c>.</param>
    /// <param name="descriptor">Content kind, media type and encoding.</param>
    /// <param name="committedAtUtc">Commit time (UTC).</param>
    /// <param name="reclaimPrevious">Deletes the previous content in the same transaction when unreferenced (system rewrites, ADR 0273 section 5).</param>
    /// <param name="cancellationToken">Cancels the interop call.</param>
    internal async ValueTask WriteAsync(
        IndexedDbFileSystemEntryRecord entry,
        string contentHash,
        long length,
        int chunkCount,
        FileSystemContentDescriptor descriptor,
        DateTimeOffset committedAtUtc,
        bool reclaimPrevious,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            PublishBoundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanWrite(
                entry,
                contentHash,
                length,
                descriptor,
                committedAtUtc,
                chunkCount,
                reclaimPrevious),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically moves or renames one entry link and updates observed revisions.</summary>
    internal async ValueTask MoveAsync(
        IndexedDbFileSystemEntryRecord entry,
        IndexedDbFileSystemEntryRecord sourceParent,
        FileSystemEntryName sourceName,
        IndexedDbFileSystemEntryRecord destinationParent,
        FileSystemEntryName destinationName,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanMove(
                entry,
                sourceParent,
                sourceName,
                destinationParent,
                destinationName,
                committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically deletes one entry or complete observed subtree.</summary>
    internal async ValueTask DeleteAsync(
        IndexedDbFileSystemEntryRecord parent,
        IReadOnlyList<IndexedDbFileSystemDeletionEntry> removals,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanDelete(parent, removals, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically copies one complete observed subtree and updates its destination parent.</summary>
    internal async ValueTask CopyAsync(
        IndexedDbFileSystemEntryRecord destinationParent,
        IReadOnlyList<IndexedDbFileSystemCopyEntry> copies,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanCopy(destinationParent, copies, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();
}