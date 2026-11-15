using System.Security.Cryptography;
using Kazo.Os.Infrastructure.Browser.FileSystem;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// An in-memory <see cref="IFileContentChunkStore"/> that checks what <c>fileContent.js</c> checks:
/// each chunk read is verified by its own SHA-256, its number and its length (ADR 0272 §4).
/// </summary>
internal sealed class MemoryFileContentChunkStore : IFileContentChunkStore
{
    /// <summary>Gets the stored chunks by <c>hash/index</c>.</summary>
    public Dictionary<string, StoredChunk> Chunks { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets how many write calls (transactions) were made.</summary>
    public int WriteCalls { get; private set; }

    /// <summary>Gets how many read calls (transactions) were made.</summary>
    public int ReadCalls { get; private set; }

    /// <summary>Gets how many chunks every read call loaded in total.</summary>
    public int ChunksRead { get; private set; }

    /// <summary>Gets the most chunks one call wrote or read.</summary>
    public int LargestCallChunks { get; private set; }

    /// <summary>Gets how many hash calls were made.</summary>
    public int HashCalls { get; private set; }

    /// <summary>Gets how many pending writes were started (ADR 0275 §4).</summary>
    public int StagedWrites { get; private set; }

    /// <summary>Gets how many pending writes are still held (not disposed).</summary>
    public int OpenStagedWrites { get; private set; }

    /// <summary>Gets the most bytes one append handed over.</summary>
    public int LargestAppend { get; private set; }

    public ValueTask<string> HashAsync(ArraySegment<byte> bytes, CancellationToken cancellationToken)
    {
        HashCalls++;
        return ValueTask.FromResult(Hex(bytes));
    }

    public ValueTask<int> WriteChunksAsync(
        string contentHash,
        int firstIndex,
        ArraySegment<byte> bytes,
        int chunkSize,
        long createdUtcMs,
        CancellationToken cancellationToken)
    {
        WriteCalls++;
        int added = 0;
        int count = 0;
        for (int offset = 0; offset < bytes.Count || count == 0; offset += chunkSize, count++)
        {
            byte[] data = bytes.Slice(offset, Math.Min(chunkSize, bytes.Count - offset)).ToArray();
            if (Chunks.TryAdd(Key(contentHash, firstIndex + count), new StoredChunk(data, Hex(data), createdUtcMs)))
            {
                added++;
            }
        }

        LargestCallChunks = Math.Max(LargestCallChunks, count);
        return ValueTask.FromResult(added);
    }

    public ValueTask ReadChunksAsync(
        string contentHash,
        int firstIndex,
        int count,
        ArraySegment<byte> destination,
        int chunkSize,
        CancellationToken cancellationToken)
    {
        ReadCalls++;
        ChunksRead += count;
        LargestCallChunks = Math.Max(LargestCallChunks, count);
        List<byte[]> chunks = [];
        for (int index = 0; index < count; index++)
        {
            if (!Chunks.TryGetValue(Key(contentHash, firstIndex + index), out StoredChunk? chunk))
            {
                throw new InvalidDataException($"Persisted file content is damaged: chunk {firstIndex + index} is missing.");
            }

            if (chunk.Data.Length > chunkSize || (index < count - 1 && chunk.Data.Length != chunkSize))
            {
                throw new InvalidDataException("Persisted file content is damaged: wrong length.");
            }

            if (Hex(chunk.Data) != chunk.ChunkHash)
            {
                throw new InvalidDataException("Persisted file content is damaged: hash.");
            }

            chunks.Add(chunk.Data);
        }

        if (chunks.Sum(chunk => chunk.Length) != destination.Count)
        {
            throw new InvalidDataException("Persisted file content is damaged: total length.");
        }

        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(destination.AsSpan(offset));
            offset += chunk.Length;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IStagedFileContent> BeginStagedWriteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StagedWrites++;
        OpenStagedWrites++;
        return ValueTask.FromResult<IStagedFileContent>(new Staged(this));
    }

    /// <summary>Stores <paramref name="text"/> as one chunk of its own hash, the way a write would.</summary>
    public string Put(byte[] content)
    {
        string hash = Hex(content);
        Chunks[Key(hash, 0)] = new StoredChunk(content, hash, 1);
        return hash;
    }

    /// <summary>Stores <paramref name="content"/> as chunks of <paramref name="chunkSize"/> bytes, as the repository does.</summary>
    public string PutChunked(byte[] content, int chunkSize)
    {
        string hash = Hex(content);
        int count = Math.Max(1, (content.Length + chunkSize - 1) / chunkSize);
        for (int index = 0; index < count; index++)
        {
            byte[] data = content.AsSpan(index * chunkSize, Math.Min(chunkSize, content.Length - (index * chunkSize))).ToArray();
            Chunks[Key(hash, index)] = new StoredChunk(data, Hex(data), 1);
        }

        return hash;
    }

    public static string Key(string contentHash, int index) => $"{contentHash}/{index}";

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>One stored chunk.</summary>
    internal sealed record StoredChunk(byte[] Data, string ChunkHash, long CreatedUtcMs);

    /// <summary>A pending write kept in a <see cref="MemoryStream"/>, with the checks of <c>fileContent.js</c>.</summary>
    private sealed class Staged(MemoryFileContentChunkStore store) : IStagedFileContent
    {
        private readonly MemoryStream _bytes = new();
        private bool _hashed;
        private bool _disposed;

        public long Length => _bytes.Length;

        public ValueTask AppendAsync(ArraySegment<byte> bytes, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hashed)
            {
                throw new InvalidOperationException("A pending file write cannot grow after it was hashed.");
            }

            store.LargestAppend = Math.Max(store.LargestAppend, bytes.Count);
            _bytes.Write(bytes);
            return ValueTask.CompletedTask;
        }

        public ValueTask<string> HashAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hashed = true;
            store.HashCalls++;
            return ValueTask.FromResult(Hex(_bytes.GetBuffer().AsSpan(0, (int)_bytes.Length)));
        }

        public ValueTask<int> WriteChunksAsync(
            string contentHash,
            int firstIndex,
            int count,
            int chunkSize,
            long createdUtcMs,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hashed)
            {
                throw new InvalidOperationException("A pending file write must be hashed before its chunks are stored.");
            }

            int length = (int)_bytes.Length;
            long start = (long)firstIndex * chunkSize;
            long end = Math.Min(length, (long)(firstIndex + count) * chunkSize);
            if (start >= end && !(start == 0 && length == 0))
            {
                throw new InvalidOperationException("The chunks of a pending file write are out of range.");
            }

            return store.WriteChunksAsync(
                contentHash,
                firstIndex,
                new ArraySegment<byte>(_bytes.GetBuffer(), (int)start, (int)(end - start)),
                chunkSize,
                createdUtcMs,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                store.OpenStagedWrites--;
                _bytes.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
