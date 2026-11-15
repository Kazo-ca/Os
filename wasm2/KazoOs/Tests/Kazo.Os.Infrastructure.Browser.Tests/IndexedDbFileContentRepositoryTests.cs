using System.Security.Cryptography;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// Verifies how file content is written and read in batches (ADR 0245) through the binary chunk
/// store (ADR 0272): one call per transaction, each chunk checked by its own hash.
/// </summary>
public sealed class IndexedDbFileContentRepositoryTests
{
    private const int ChunkBytes = 256 * 1024;
    private const int BatchBytes = FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes;

    [Fact]
    public async Task WriteAsync_FileOfOneBatch_UsesOneTransaction()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);

        await repository.WriteAsync(new MemoryStream(Pattern(FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes)));

        Assert.Equal(1, store.WriteCalls);
        Assert.Equal(FileContentStoragePolicy.ChunksPerTransaction, store.Chunks.Count);
    }

    [Fact]
    public async Task WriteAsync_FileLargerThanOneBatch_IsWrittenInSeveralTransactions()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        int length = (2 * FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes) + 1;

        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(Pattern(length)));

        Assert.Equal(3, store.WriteCalls);
        Assert.True(store.LargestCallChunks <= FileContentStoragePolicy.ChunksPerTransaction);
        Assert.Equal((2 * FileContentStoragePolicy.ChunksPerTransaction) + 1, store.Chunks.Count);
        Assert.Equal(length, write.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Pattern(length))).ToLowerInvariant(), write.ContentHash);
    }

    [Fact]
    public async Task WriteAsync_EachChunkKeepsTheHashOfItsOwnBytes()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] original = Pattern((2 * ChunkBytes) + 10);

        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(original));

        MemoryFileContentChunkStore.StoredChunk last = store.Chunks[MemoryFileContentChunkStore.Key(write.ContentHash, 2)];
        Assert.Equal(10, last.Data.Length);
        Assert.Equal(MemoryFileContentChunkStore.Hex(original.AsSpan(2 * ChunkBytes)), last.ChunkHash);
    }

    [Fact]
    public async Task WriteAsync_EmptyFile_StoresOneEmptyChunk()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);

        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream());
        await using Stream read = await repository.ReadAsync(write.ContentHash, 0);

        Assert.Equal(0, write.Length);
        Assert.Empty(Assert.Single(store.Chunks).Value.Data);
        Assert.Equal(0, read.Length);
    }

    [Fact]
    public async Task WriteAsync_NonSeekableStream_IsReadToTheEnd()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] original = Pattern(ChunkBytes + 3);

        IndexedDbFileContentWrite write = await repository.WriteAsync(new ForwardOnlyStream(original));

        Assert.Equal(original.Length, write.Length);
        Assert.Equal(MemoryFileContentChunkStore.Hex(original), write.ContentHash);
    }

    [Fact]
    public async Task ReadAsync_FileLargerThanOneBatch_ReadsBackTheSameBytesInBatches()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] original = Pattern((FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes) + 12345);
        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(original));

        await using Stream read = await repository.ReadAsync(write.ContentHash, write.Length);

        using MemoryStream copy = new();
        await read.CopyToAsync(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Equal(2, store.ReadCalls);
        Assert.True(store.LargestCallChunks <= FileContentStoragePolicy.ChunksPerTransaction);
    }

    [Fact]
    public async Task ReadAsync_DoesNotHashTheWholeFileAgain()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(Pattern(3 * ChunkBytes)));

        await using Stream read = await repository.ReadAsync(write.ContentHash, write.Length);

        Assert.Equal(1, store.HashCalls);
    }

    [Fact]
    public async Task ReadAsync_LargeFileWithAMissingChunk_IsRejected()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        IndexedDbFileContentWrite write = await repository.WriteAsync(
            new MemoryStream(Pattern((FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes) + 1)));
        store.Chunks.Remove(MemoryFileContentChunkStore.Key(write.ContentHash, FileContentStoragePolicy.ChunksPerTransaction));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ReadAsync(write.ContentHash, write.Length));
    }

    [Fact]
    public async Task ReadAsync_ChunkThatNoLongerMatchesItsHash_IsRejected()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(Pattern(2 * ChunkBytes)));
        store.Chunks[MemoryFileContentChunkStore.Key(write.ContentHash, 1)].Data[7] ^= 0xFF;

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ReadAsync(write.ContentHash, write.Length));
    }

    [Fact]
    public async Task ReadAsync_LengthThatDoesNotMatchTheChunks_IsRejected()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(Pattern(ChunkBytes + 5)));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ReadAsync(write.ContentHash, write.Length + 1));
    }

    [Fact]
    public async Task WriteAsync_FileOfExactlyTheLimit_IsAcceptedAndOneMoreByteIsRefused()
    {
        MemoryFileContentChunkStore store = new();
        FileContentStoragePolicy policy = new(4 * ChunkBytes, ChunkBytes, "SHA-256", true, TimeSpan.FromDays(1));
        await using IndexedDbFileContentRepository repository = new(new ModuleRuntime(), policy, chunks: store);

        await repository.WriteAsync(new MemoryStream(Pattern(4 * ChunkBytes)));
        await Assert.ThrowsAsync<FileContentTooLargeException>(
            async () => await repository.WriteAsync(new MemoryStream(Pattern((4 * ChunkBytes) + 1))));
        await Assert.ThrowsAsync<FileContentTooLargeException>(
            async () => await repository.WriteAsync(new ForwardOnlyStream(Pattern((4 * ChunkBytes) + 1))));
        Assert.Equal(1, store.WriteCalls);
    }

    [Fact]
    public async Task WriteAsync_FileOfOneBatch_IsStoredWithoutAPendingWrite()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);

        await repository.WriteAsync(new MemoryStream(Pattern(BatchBytes)));
        await repository.WriteAsync(new ForwardOnlyStream(Pattern(ChunkBytes + 7)));

        Assert.Equal(0, store.StagedWrites);
    }

    [Fact]
    public async Task WriteAsync_FileLargerThanOneBatch_IsHandedToTheBrowserOneBatchAtATime()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] original = Pattern((2 * BatchBytes) + 12345);

        IndexedDbFileContentWrite write = await repository.WriteAsync(new ForwardOnlyStream(original));
        await using Stream read = await repository.ReadAsync(write.ContentHash, write.Length);

        Assert.Equal(1, store.StagedWrites);
        Assert.Equal(0, store.OpenStagedWrites);
        Assert.Equal(BatchBytes + 1, store.LargestAppend);
        Assert.Equal(3, store.WriteCalls);
        Assert.Equal(MemoryFileContentChunkStore.Hex(original), write.ContentHash);
        Assert.Equal(original, ((MemoryStream)read).ToArray());
    }

    [Fact]
    public async Task WriteAsync_StreamThatGrowsPastItsLength_IsStoredWhole()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] original = Pattern(BatchBytes + ChunkBytes + 3);

        IndexedDbFileContentWrite write = await repository.WriteAsync(new UnderstatedLengthStream(original, 10));

        Assert.Equal(original.Length, write.Length);
        Assert.Equal(MemoryFileContentChunkStore.Hex(original), write.ContentHash);
        Assert.Equal(BatchBytes + 1, store.LargestAppend);
    }

    [Fact]
    public async Task WriteAsync_StreamPastTheLimitAfterSeveralBatches_StoresNothingAndReleasesThePendingWrite()
    {
        MemoryFileContentChunkStore store = new();
        long limit = BatchBytes + ChunkBytes;
        FileContentStoragePolicy policy = new(limit, ChunkBytes, "SHA-256", true, TimeSpan.FromDays(1));
        await using IndexedDbFileContentRepository repository = new(new ModuleRuntime(), policy, chunks: store);

        await Assert.ThrowsAsync<FileContentTooLargeException>(
            async () => await repository.WriteAsync(new ForwardOnlyStream(Pattern((int)limit + 1))));

        Assert.Equal(0, store.WriteCalls);
        Assert.Empty(store.Chunks);
        Assert.Equal(1, store.StagedWrites);
        Assert.Equal(0, store.OpenStagedWrites);
    }

    [Fact]
    public async Task WriteAsync_SourceFailingAfterTheFirstBatch_ReleasesThePendingWrite()
    {
        MemoryFileContentChunkStore store = new();
        await using IndexedDbFileContentRepository repository = Repository(store);

        await Assert.ThrowsAsync<IOException>(
            async () => await repository.WriteAsync(new FailingStream(Pattern(BatchBytes + ChunkBytes), BatchBytes)));

        Assert.Equal(0, store.WriteCalls);
        Assert.Equal(0, store.OpenStagedWrites);
    }

    private static IndexedDbFileContentRepository Repository(MemoryFileContentChunkStore store) =>
        new(new ModuleRuntime(), chunks: store);

    [Fact]
    public async Task WriteAsync_ReusingStoredContent_RefreshesItsCreationTime()
    {
        MemoryFileContentChunkStore store = new();
        MovableTimeProvider time = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        await using IndexedDbFileContentRepository repository = new(
            new ModuleRuntime(), FileContentStoragePolicy.Default, time, chunks: store);
        byte[] content = Pattern((2 * ChunkBytes) + 1);
        IndexedDbFileContentWrite first = await repository.WriteAsync(new MemoryStream(content));

        // Two days later the old content is an orphan past its retention; writing the same bytes
        // again reuses it and must make it young, or a concurrent orphan pass could delete it.
        time.Now = time.Now.AddDays(2);
        await repository.WriteAsync(new MemoryStream(content));

        long expected = time.Now.ToUnixTimeMilliseconds();
        Assert.Equal(expected, store.Chunks[MemoryFileContentChunkStore.Key(first.ContentHash, 0)].CreatedUtcMs);
        Assert.Equal(3, store.Chunks.Count);
        Assert.Equal(3, first.ChunkCount);
    }

    private sealed class MovableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * 31) ^ (index >> 8));
        }

        return bytes;
    }

    /// <summary>The JSON adapter's runtime: only opens the database here.</summary>
    internal sealed class ModuleRuntime : IJSRuntime, IJSObjectReference
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => identifier == "import"
                ? ValueTask.FromResult((TValue)(object)this)
                : identifier == "openDatabase"
                    ? ValueTask.FromResult(default(TValue)!)
                    : throw new InvalidOperationException($"The content must not go through JSON ({identifier}).");
    }

    /// <summary>A stream that can only be read forward, of unknown length.</summary>
    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1000));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A seekable stream whose length says less than it holds, like a file that grows while it is read.</summary>
    private sealed class UnderstatedLengthStream(byte[] content, long statedLength) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => statedLength;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A forward-only stream that fails once <paramref name="failAfter"/> bytes were read.</summary>
    private sealed class FailingStream(byte[] content, int failAfter) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Position >= failAfter
            ? throw new IOException("The source broke.")
            : _inner.Read(buffer, offset, (int)Math.Min(count, failAfter - _inner.Position));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
