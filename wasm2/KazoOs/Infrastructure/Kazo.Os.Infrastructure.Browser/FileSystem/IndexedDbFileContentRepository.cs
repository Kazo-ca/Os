using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>Persists and reconstructs immutable, SHA-256-addressed file chunks.</summary>
internal sealed class IndexedDbFileContentRepository : IAsyncDisposable
{
    private const string Boundary = "FileSystemContentWrite";
    private readonly IndexedDbInteropAdapter _adapter;
    private readonly FileContentStoragePolicy _policy;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the content repository with the accepted browser storage policy.</summary>
    /// <param name="runtime">JavaScript runtime.</param>
    /// <param name="policy">Chunk and file size limits.</param>
    /// <param name="timeProvider">Clock stamping chunk creation times.</param>
    /// <param name="disk">
    /// Optional statistics of <c>hda</c>: a content write counts the bytes of its chunks, a content
    /// read the bytes of the file (ADR 0057 section 4).
    /// </param>
    internal IndexedDbFileContentRepository(
        IJSRuntime runtime,
        FileContentStoragePolicy? policy = null,
        TimeProvider? timeProvider = null,
        IBlockDeviceStatistics? disk = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime, disk);
        _policy = policy ?? FileContentStoragePolicy.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Consumes a stream once, hashes it incrementally, and stores deduplicated chunks.</summary>
    internal async ValueTask<IndexedDbFileContentWrite> WriteAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new InvalidDataException("Filesystem content source is not readable.");
        }

        List<byte[]> chunks = [];
        long length = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked((int)_policy.MaxChunkSizeBytes));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(
                    buffer.AsMemory(0, checked((int)_policy.MaxChunkSizeBytes)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                if (length > _policy.MaxFileSizeBytes)
                {
                    throw new FileContentTooLargeException(_policy.MaxFileSizeBytes);
                }

                hash.AppendData(buffer, 0, read);
                chunks.Add(buffer.AsSpan(0, read).ToArray());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (chunks.Count == 0)
        {
            chunks.Add([]);
        }

        string contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);

        // A file of up to 128 MiB is written in batches of ChunksPerTransaction chunks (ADR 0245):
        // only one batch is base64-encoded and crosses the JS interop at a time. The chunks are
        // addressed by the hash of the whole content, so an interrupted write leaves only
        // unreferenced chunks that the orphan cleanup removes, and a retry adds the missing ones.
        long createdUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        for (int first = 0; first < chunks.Count; first += FileContentStoragePolicy.ChunksPerTransaction)
        {
            int count = Math.Min(FileContentStoragePolicy.ChunksPerTransaction, chunks.Count - first);
            IReadOnlyList<IndexedDbOperation> operations = [.. Enumerable.Range(first, count).Select(index =>
                new IndexedDbOperation(
                    "addIfAbsent",
                    KazoOsIndexedDbSchema.FileContentStoreName,
                    Key: new object[] { contentHash, index },
                    Value: new IndexedDbFileContentChunkRecord(
                        contentHash,
                        index,
                        Convert.ToBase64String(chunks[index]),
                        createdUtcMs)))];
            long batchBytes = 0;
            for (int index = first; index < first + count; index++)
            {
                batchBytes += chunks[index].Length;
            }

            await _adapter.ExecuteAsync(
                Boundary,
                IndexedDbTransactionMode.ReadWrite,
                operations,
                cancellationToken,
                ioBytes: batchBytes).ConfigureAwait(false);
        }

        return new IndexedDbFileContentWrite(contentHash, length);
    }

    /// <summary>Loads and validates all chunks for one immutable content hash.</summary>
    internal async ValueTask<Stream> ReadAsync(
        string contentHash,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        MemoryStream content = new(checked((int)expectedLength));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            int expectedChunks = expectedLength == 0 ? 1 : _policy.ChunkCountFor(expectedLength);
            if (expectedChunks <= FileContentStoragePolicy.ChunksPerTransaction)
            {
                // A file of at most one batch is read with one index query, as before ADR 0245.
                IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
                    Boundary,
                    IndexedDbTransactionMode.ReadOnly,
                    [new IndexedDbOperation(
                        "getAll",
                        KazoOsIndexedDbSchema.FileContentStoreName,
                        IndexName: "contentHash",
                        Query: contentHash)],
                    cancellationToken,
                    ioBytes: expectedLength).ConfigureAwait(false);
                JsonElement records = results.Single();
                if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
                {
                    throw new InvalidDataException("Persisted file content has no chunks.");
                }

                await AppendChunksAsync(content, hash, contentHash, 0, records.EnumerateArray(), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // A larger file is read in batches of ChunksPerTransaction keys (ADR 0245), so the
                // interop never carries the base64 copy of the whole file at once.
                for (int first = 0; first < expectedChunks; first += FileContentStoragePolicy.ChunksPerTransaction)
                {
                    int count = Math.Min(FileContentStoragePolicy.ChunksPerTransaction, expectedChunks - first);
                    IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
                        Boundary,
                        IndexedDbTransactionMode.ReadOnly,
                        [.. Enumerable.Range(first, count).Select(index => new IndexedDbOperation(
                            "get",
                            KazoOsIndexedDbSchema.FileContentStoreName,
                            Key: new object[] { contentHash, index }))],
                        cancellationToken,
                        ioBytes: Math.Min(
                            (long)count * _policy.MaxChunkSizeBytes,
                            expectedLength - (first * _policy.MaxChunkSizeBytes))).ConfigureAwait(false);
                    if (results.Count != count || results.Any(record => record.ValueKind != JsonValueKind.Object))
                    {
                        throw new InvalidDataException("Persisted file chunks are incomplete or out of order.");
                    }

                    await AppendChunksAsync(content, hash, contentHash, first, results, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            content.Dispose();
            throw;
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (content.Length != expectedLength
            || !StringComparer.Ordinal.Equals(actualHash, contentHash))
        {
            content.Dispose();
            throw new InvalidDataException("Persisted file content does not match its metadata hash and length.");
        }

        content.Position = 0;
        return content;
    }

    /// <summary>
    /// Appends a run of chunk records to <paramref name="content"/>, checking that they belong to
    /// <paramref name="contentHash"/> and follow each other from <paramref name="firstIndex"/>.
    /// </summary>
    private static async ValueTask AppendChunksAsync(
        MemoryStream content,
        IncrementalHash hash,
        string contentHash,
        int firstIndex,
        IEnumerable<JsonElement> records,
        CancellationToken cancellationToken)
    {
        int expectedIndex = firstIndex;
        foreach (JsonElement element in records)
        {
            IndexedDbFileContentChunkRecord chunk = IndexedDbFileContentChunkRecord.FromJson(element);
            if (!StringComparer.Ordinal.Equals(chunk.ContentHash, contentHash)
                || chunk.ChunkIndex != expectedIndex++)
            {
                throw new InvalidDataException("Persisted file chunks are incomplete or out of order.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(chunk.DataBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Persisted file chunk data is malformed.", exception);
            }

            hash.AppendData(bytes);
            await content.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

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
