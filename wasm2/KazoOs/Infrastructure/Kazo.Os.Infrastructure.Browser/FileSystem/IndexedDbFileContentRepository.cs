using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// Persists and reconstructs immutable, SHA-256-addressed file chunks. Since ADR 0272 the chunks
/// are binary (<c>Uint8Array</c> with the SHA-256 of each chunk) and the bytes go through
/// <see cref="IFileContentChunkStore"/>, without JSON or base64; the JSON adapter only opens the
/// database and runs the orphan cleanup.
/// </summary>
internal sealed class IndexedDbFileContentRepository : IAsyncDisposable
{
    private const string Boundary = "FileSystemContentWrite";
    private readonly IndexedDbInteropAdapter _adapter;
    private readonly IFileContentChunkStore _chunks;
    private readonly FileContentStoragePolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly IBlockDeviceStatistics? _disk;

    /// <summary>Initializes the content repository with the accepted browser storage policy.</summary>
    /// <param name="runtime">JavaScript runtime of the JSON adapter.</param>
    /// <param name="policy">Chunk and file size limits.</param>
    /// <param name="timeProvider">Clock stamping chunk creation times.</param>
    /// <param name="disk">
    /// Optional statistics of <c>hda</c>: a content write counts the bytes of its chunks, a content
    /// read the bytes of the file, one I/O per transaction (ADR 0057 section 4).
    /// </param>
    /// <param name="chunks">
    /// Binary chunk store; <see cref="BrowserFileContentChunkStore"/> in the browser when omitted.
    /// </param>
    internal IndexedDbFileContentRepository(
        IJSRuntime runtime,
        FileContentStoragePolicy? policy = null,
        TimeProvider? timeProvider = null,
        IBlockDeviceStatistics? disk = null,
        IFileContentChunkStore? chunks = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime, disk);
        _chunks = chunks ?? DefaultChunkStore();
        _policy = policy ?? FileContentStoragePolicy.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _disk = disk;
    }

    /// <summary>Gets the bytes of one chunk; only the last chunk of a file may be shorter.</summary>
    internal int ChunkSize => checked((int)_policy.MaxChunkSizeBytes);

    /// <summary>
    /// Consumes a stream once and stores its deduplicated chunks: the SHA-256 of the file and of
    /// each chunk are computed by the browser (ADR 0272 §2).
    /// </summary>
    internal async ValueTask<IndexedDbFileContentWrite> WriteAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new InvalidDataException("Filesystem content source is not readable.");
        }

        byte[] content = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        string contentHash = await _chunks.HashAsync(new ArraySegment<byte>(content), cancellationToken)
            .ConfigureAwait(false);

        // A file of up to 128 MiB is written in batches of ChunksPerTransaction chunks (ADR 0245).
        // The chunks are addressed by the hash of the whole content, so an interrupted write leaves
        // only unreferenced chunks that the orphan cleanup removes, and a retry adds the missing ones.
        long createdUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        int chunkCount = ChunkCountFor(content.Length);
        for (int first = 0; first < chunkCount; first += FileContentStoragePolicy.ChunksPerTransaction)
        {
            int count = Math.Min(FileContentStoragePolicy.ChunksPerTransaction, chunkCount - first);
            ArraySegment<byte> batch = BatchOf(content, first, count);
            await MeasureAsync(
                BlockIoDirection.Write,
                batch.Count,
                () => _chunks.WriteChunksAsync(contentHash, first, batch, ChunkSize, createdUtcMs, cancellationToken))
                .ConfigureAwait(false);
        }

        return new IndexedDbFileContentWrite(contentHash, content.Length);
    }

    /// <summary>
    /// Loads all chunks for one immutable content hash, checking each one by its own hash, their
    /// numbers and the total length (ADR 0272 §4).
    /// </summary>
    internal async ValueTask<Stream> ReadAsync(
        string contentHash,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        ValidateLength(contentHash, expectedLength);
        byte[] content = new byte[checked((int)expectedLength)];
        if (expectedLength == 0)
        {
            // An empty file still has one, empty, chunk, read and checked like the others.
            await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
            await ReadChunkSpanAsync(contentHash, 0, 1, new ArraySegment<byte>(content), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await ReadRangeAsync(contentHash, expectedLength, 0, new ArraySegment<byte>(content), cancellationToken)
                .ConfigureAwait(false);
        }

        return new MemoryStream(content, 0, content.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Reads the bytes of a content from <paramref name="offset"/> into <paramref name="destination"/>
    /// (ADR 0275 §1): only the chunks that cover the range are loaded, in batches of at most
    /// <see cref="FileContentStoragePolicy.ChunksPerTransaction"/> chunks, each one checked by its
    /// own hash (ADR 0272 §4).
    /// </summary>
    /// <param name="contentHash">SHA-256 of the whole file.</param>
    /// <param name="fileLength">Length of the whole file, as its entry records it.</param>
    /// <param name="offset">First byte to read; at or past the end, nothing is read.</param>
    /// <param name="destination">Receives the bytes; fewer are read when the file ends before it is full.</param>
    /// <param name="cancellationToken">Observed between batches.</param>
    /// <returns>How many bytes were read.</returns>
    /// <exception cref="InvalidDataException">A chunk of the range is missing, out of order or damaged.</exception>
    internal async ValueTask<int> ReadRangeAsync(
        string contentHash,
        long fileLength,
        long offset,
        ArraySegment<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ValidateLength(contentHash, fileLength);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        int count = (int)Math.Min(destination.Count, Math.Max(0, fileLength - offset));
        if (count == 0)
        {
            return 0;
        }

        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        long end = offset + count;
        int firstChunk = (int)(offset / ChunkSize);
        int lastChunk = (int)((end - 1) / ChunkSize);
        long spanStart = (long)firstChunk * ChunkSize;
        long spanEnd = Math.Min(fileLength, ((long)lastChunk + 1) * ChunkSize);
        ArraySegment<byte> target = destination.Slice(0, count);
        if (spanStart == offset && spanEnd == end)
        {
            // Aligned on chunks (what the read stream asks for): the chunks land in place.
            await ReadChunkSpanAsync(contentHash, firstChunk, lastChunk - firstChunk + 1, target, cancellationToken)
                .ConfigureAwait(false);
            return count;
        }

        int spanLength = (int)(spanEnd - spanStart);
        byte[] span = ArrayPool<byte>.Shared.Rent(spanLength);
        try
        {
            await ReadChunkSpanAsync(
                contentHash,
                firstChunk,
                lastChunk - firstChunk + 1,
                new ArraySegment<byte>(span, 0, spanLength),
                cancellationToken).ConfigureAwait(false);
            span.AsSpan((int)(offset - spanStart), count).CopyTo(target);
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(span);
        }
    }

    /// <summary>
    /// Reads the consecutive chunks <paramref name="firstChunk"/> to <paramref name="firstChunk"/> +
    /// <paramref name="chunkCount"/> into <paramref name="destination"/>, which holds exactly their
    /// bytes, one transaction per batch.
    /// </summary>
    private async ValueTask ReadChunkSpanAsync(
        string contentHash,
        int firstChunk,
        int chunkCount,
        ArraySegment<byte> destination,
        CancellationToken cancellationToken)
    {
        int written = 0;
        for (int done = 0; done < chunkCount; done += FileContentStoragePolicy.ChunksPerTransaction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(FileContentStoragePolicy.ChunksPerTransaction, chunkCount - done);
            int length = (int)Math.Min(destination.Count - written, (long)count * ChunkSize);
            ArraySegment<byte> batch = destination.Slice(written, length);
            int first = firstChunk + done;
            await MeasureAsync(
                BlockIoDirection.Read,
                batch.Count,
                async () =>
                {
                    await _chunks.ReadChunksAsync(contentHash, first, count, batch, ChunkSize, cancellationToken)
                        .ConfigureAwait(false);
                    return 0;
                }).ConfigureAwait(false);
            written += length;
        }
    }

    private void ValidateLength(string contentHash, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > _policy.MaxFileSizeBytes)
        {
            throw new InvalidDataException("Persisted file length exceeds the browser limit.");
        }
    }

    /// <summary>Number of chunks of a file: an empty file still has one, empty, chunk.</summary>
    private int ChunkCountFor(int length) => length == 0 ? 1 : _policy.ChunkCountFor(length);

    /// <summary>The bytes of the chunks <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/>.</summary>
    private ArraySegment<byte> BatchOf(byte[] content, int first, int count)
    {
        int offset = (int)Math.Min(content.Length, (long)first * ChunkSize);
        int length = (int)Math.Min(content.Length - offset, (long)count * ChunkSize);
        return new ArraySegment<byte>(content, offset, length);
    }

    /// <summary>
    /// Reads the whole source, refusing more than <see cref="FileContentStoragePolicy.MaxFileSizeBytes"/>
    /// before anything is stored (ADR 0245).
    /// </summary>
    private async ValueTask<byte[]> ReadAllAsync(Stream source, CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            long remaining = Math.Max(0, source.Length - source.Position);
            if (remaining > _policy.MaxFileSizeBytes)
            {
                throw new FileContentTooLargeException(_policy.MaxFileSizeBytes);
            }

            byte[] exact = new byte[remaining];
            int filled = 0;
            while (filled < exact.Length)
            {
                int read = await source.ReadAsync(exact.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return exact.AsSpan(0, filled).ToArray();
                }

                filled += read;
            }

            // A stream that grows while it is read is finished the generic way.
            byte[] probe = new byte[1];
            if (await source.ReadAsync(probe, cancellationToken).ConfigureAwait(false) == 0)
            {
                return exact;
            }

            using MemoryStream rest = new();
            rest.Write(exact);
            rest.Write(probe);
            await CopyBoundedAsync(source, rest, cancellationToken).ConfigureAwait(false);
            return rest.ToArray();
        }

        using MemoryStream buffer = new();
        await CopyBoundedAsync(source, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async ValueTask CopyBoundedAsync(Stream source, MemoryStream destination, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                if (destination.Length + read > _policy.MaxFileSizeBytes)
                {
                    throw new FileContentTooLargeException(_policy.MaxFileSizeBytes);
                }

                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Counts one transaction of the chunk store in <c>/proc/diskstats</c>, like the JSON adapter
    /// does for its own (ADR 0057 §4): in flight from the call to its end, with its bytes.
    /// </summary>
    private async ValueTask MeasureAsync(BlockIoDirection direction, long bytes, Func<ValueTask<int>> operation)
    {
        if (_disk is null)
        {
            await operation().ConfigureAwait(false);
            return;
        }

        long started = Stopwatch.GetTimestamp();
        bool succeeded = false;
        _disk.Begin();
        try
        {
            await operation().ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            _disk.Complete(direction, bytes, Stopwatch.GetElapsedTime(started), succeeded);
        }
    }

    private static IFileContentChunkStore DefaultChunkStore() => OperatingSystem.IsBrowser()
        ? BrowserFileContentChunkStore.Instance
        : UnavailableFileContentChunkStore.Instance;

    /// <summary>Deletes a bounded set of retained hashes after atomic reference revalidation.</summary>
    internal async ValueTask<IndexedDbFileContentCleanupResult> CleanupAsync(
        int maximumHashes = 64,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHashes);
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> discovery = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("getAll", KazoOsIndexedDbSchema.FileContentStoreName)],
            cancellationToken).ConfigureAwait(false);
        long cutoffUtcMs = (_timeProvider.GetUtcNow() - _policy.OrphanRetention).ToUnixTimeMilliseconds();
        IndexedDbFileContentChunkRecord[] chunks = discovery.Single().ValueKind == JsonValueKind.Array
            ? [.. discovery.Single().EnumerateArray().Select(IndexedDbFileContentChunkRecord.FromJson)]
            : throw new InvalidDataException("Persisted file chunk discovery result is malformed.");
        string[] candidates =
        [..
            chunks.GroupBy(chunk => chunk.ContentHash, StringComparer.Ordinal)
                .Where(group => group.All(chunk => chunk.CreatedUtcMs is not null)
                    && group.Max(chunk => chunk.CreatedUtcMs) <= cutoffUtcMs)
                .Select(group => group.Key)
                .Order(StringComparer.Ordinal)
                .Take(maximumHashes)
        ];
        if (candidates.Length == 0)
        {
            return new IndexedDbFileContentCleanupResult(0, 0, 0);
        }

        IReadOnlyList<IndexedDbOperation> operations = [.. candidates.Select(contentHash =>
            new IndexedDbOperation(
                "deleteAllByIndexIfUnreferenced",
                KazoOsIndexedDbSchema.FileContentStoreName,
                IndexName: "contentHash",
                Query: contentHash,
                ReferenceObjectStoreName: KazoOsIndexedDbSchema.FileSystemEntryStoreName,
                ReferenceIndexName: "contentHash"))];
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            "FileSystemContentCleanup",
            IndexedDbTransactionMode.ReadWrite,
            operations,
            cancellationToken).ConfigureAwait(false);
        int deletedChunks = results.Sum(result => result.GetProperty("deleted").GetInt32());
        int retainedHashes = results.Count(result => result.GetProperty("referenced").GetBoolean());
        return new IndexedDbFileContentCleanupResult(candidates.Length, deletedChunks, retainedHashes);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();
}

/// <summary>Reports the immutable identity and byte length of persisted file content.</summary>
internal sealed record IndexedDbFileContentWrite(string ContentHash, long Length);

/// <summary>Stable browser-storage representation of one content chunk.</summary>
internal sealed record IndexedDbFileContentChunkRecord(
    string ContentHash,
    int ChunkIndex,
    string DataBase64,
    long? CreatedUtcMs)
{
    /// <summary>Decodes one chunk without reflection-based deserialization.</summary>
    internal static IndexedDbFileContentChunkRecord FromJson(JsonElement element) => new(
        element.GetProperty("contentHash").GetString()
            ?? throw new InvalidDataException("Persisted file chunk has no content hash."),
        element.GetProperty("chunkIndex").GetInt32(),
        element.GetProperty("dataBase64").GetString()
            ?? throw new InvalidDataException("Persisted file chunk has no data."),
        element.TryGetProperty("createdUtcMs", out JsonElement createdUtcMs)
            ? createdUtcMs.GetInt64()
            : null);
}

/// <summary>Summarizes one bounded orphan-content collection pass.</summary>
public sealed record IndexedDbFileContentCleanupResult(
    int CandidateHashes,
    int DeletedChunks,
    int RetainedReferencedHashes);

/// <summary>
/// The content being stored exceeds <see cref="Schema.FileContentStoragePolicy.MaxFileSizeBytes"/>
/// (128 MiB, ADR 0245); the provider reports it as <c>FileSystemErrorCode.FileTooLarge</c>
/// (<c>EFBIG</c>). Nothing was written: the limit is checked before any chunk is stored.
/// </summary>
internal sealed class FileContentTooLargeException : IOException
{
    /// <summary>Creates the failure for a limit of <paramref name="maxFileSizeBytes"/>.</summary>
    /// <param name="maxFileSizeBytes">The largest accepted file, in bytes.</param>
    public FileContentTooLargeException(long maxFileSizeBytes)
        : base($"File content exceeds the {maxFileSizeBytes}-byte browser limit.")
    {
        MaxFileSizeBytes = maxFileSizeBytes;
    }

    /// <summary>Gets the largest accepted file, in bytes.</summary>
    public long MaxFileSizeBytes { get; }
}
