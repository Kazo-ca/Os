using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// Verifies the partial read of ADR 0275 §1 and §2: a range loads only the chunks that cover it,
/// and a large file is read through a window of chunks loaded as it is consumed.
/// </summary>
public sealed class ChunkedContentReadStreamTests
{
    private const int ChunkBytes = 256 * 1024;
    private const int Batch = FileContentStoragePolicy.ChunksPerTransaction;

    [Fact]
    public async Task ReadRangeAsync_UnalignedRange_LoadsOnlyTheChunksThatCoverIt()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern((10 * ChunkBytes) + 77);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] destination = new byte[ChunkBytes];

        int read = await repository.ReadRangeAsync(hash, original.Length, (3 * ChunkBytes) + 100, destination);

        Assert.Equal(ChunkBytes, read);
        Assert.Equal(original.AsSpan((3 * ChunkBytes) + 100, ChunkBytes).ToArray(), destination);
        Assert.Equal(2, store.ChunksRead);
        Assert.Equal(1, store.ReadCalls);
    }

    [Fact]
    public async Task ReadRangeAsync_RangePastTheEnd_StopsAtTheEndOfTheFile()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern((2 * ChunkBytes) + 5);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] destination = new byte[100];

        int tail = await repository.ReadRangeAsync(hash, original.Length, original.Length - 10, destination);
        int past = await repository.ReadRangeAsync(hash, original.Length, original.Length + 3, destination);

        Assert.Equal(10, tail);
        Assert.Equal(original.AsSpan(original.Length - 10).ToArray(), destination.AsSpan(0, 10).ToArray());
        Assert.Equal(0, past);
        Assert.Equal(2, store.ChunksRead); // the last 10 bytes straddle chunks 1 and 2
    }

    [Fact]
    public async Task ReadRangeAsync_RangeOverSeveralBatches_UsesOneTransactionPerBatch()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern((Batch + 10) * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        byte[] destination = new byte[(Batch + 2) * ChunkBytes];

        int read = await repository.ReadRangeAsync(hash, original.Length, 4 * ChunkBytes, destination);

        Assert.Equal(destination.Length, read);
        Assert.Equal(original.AsSpan(4 * ChunkBytes, destination.Length).ToArray(), destination);
        Assert.Equal(2, store.ReadCalls);
        Assert.True(store.LargestCallChunks <= Batch);
    }

    [Fact]
    public async Task ReadRangeAsync_DamagedChunkInTheRange_IsRejected()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern(4 * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        store.Chunks[MemoryFileContentChunkStore.Key(hash, 2)].Data[0] ^= 0xFF;
        await using IndexedDbFileContentRepository repository = Repository(store);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ReadRangeAsync(hash, original.Length, 2 * ChunkBytes, new byte[10]));
        Assert.Equal(1, await repository.ReadRangeAsync(hash, original.Length, 0, new byte[1]));
    }

    [Fact]
    public async Task OpenAsync_LoadsOnlyTheFirstWindow()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern((Batch * 2) * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);

        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);

        Assert.Equal(ChunkedContentReadStream.InitialWindowChunks, store.ChunksRead);
        Assert.Equal(original.Length, stream.Length);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task CopyToAsync_ReadsTheWholeFileWithAWindowThatNeverPassesOneBatch()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern((3 * Batch * ChunkBytes) + 1234);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);

        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);

        Assert.Equal(original, copy.ToArray());
        Assert.Equal((3 * Batch) + 1, store.ChunksRead);
        Assert.True(store.LargestCallChunks <= Batch);
        Assert.True(stream.WindowLength <= Batch * ChunkBytes);
    }

    [Fact]
    public async Task SequentialReads_DoubleTheWindowUpToOneBatch()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern(4 * Batch * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);
        List<int> windows = [stream.WindowLength / ChunkBytes];
        byte[] buffer = new byte[ChunkBytes];

        while (windows.Count < 7)
        {
            int previous = stream.WindowLength;
            long previousStart = stream.WindowStart;
            await stream.ReadExactlyAsync(buffer);
            if (stream.WindowStart != previousStart || stream.WindowLength != previous)
            {
                windows.Add(stream.WindowLength / ChunkBytes);
            }
        }

        Assert.Equal([4, 8, 16, 32, 64, 64, 64], windows);
    }

    [Fact]
    public async Task Seek_LoadsOnlyTheChunksNearTheNewPosition()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern((Batch * 4) * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);
        long target = (200L * ChunkBytes) + 17;

        stream.Seek(target, SeekOrigin.Begin);
        byte[] buffer = new byte[64];
        await stream.ReadExactlyAsync(buffer);

        Assert.Equal(original.AsSpan((int)target, 64).ToArray(), buffer);
        Assert.Equal(2 * ChunkedContentReadStream.InitialWindowChunks, store.ChunksRead);
        Assert.Equal(200L * ChunkBytes, stream.WindowStart);
        Assert.Equal(target + 64, stream.Position);
    }

    [Fact]
    public async Task SeekBackInsideTheWindow_LoadsNothing()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern(Batch * 2 * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);
        byte[] buffer = new byte[1000];
        await stream.ReadExactlyAsync(buffer);
        int loaded = store.ChunksRead;

        stream.Position = 10;
        await stream.ReadExactlyAsync(buffer);

        Assert.Equal(loaded, store.ChunksRead);
        Assert.Equal(original.AsSpan(10, 1000).ToArray(), buffer);
    }

    [Fact]
    public async Task SynchronousRead_IsServedFromTheWindowAndRefusedPastIt()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern(Batch * 2 * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);
        byte[] buffer = new byte[16];

        int read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = original.Length - 1;

        Assert.Equal(16, read);
        Assert.Equal(original.AsSpan(0, 16).ToArray(), buffer);
        Assert.Throws<NotSupportedException>(() => stream.Read(buffer, 0, 1));
    }

    [Fact]
    public async Task ReadAtOrPastTheEnd_ReturnsZero()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern((Batch * ChunkBytes) + 3);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);

        stream.Seek(0, SeekOrigin.End);
        Assert.Equal(0, await stream.ReadAsync(new byte[10]));
        stream.Seek(5, SeekOrigin.End);
        Assert.Equal(0, await stream.ReadAsync(new byte[10]));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public async Task ChunkLostAfterOpening_FailsTheReadWithAnIOException()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern(Batch * 2 * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        await using ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);
        store.Chunks.Remove(MemoryFileContentChunkStore.Key(hash, 100));

        stream.Position = 100L * ChunkBytes;
        IOException failure = await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[1]));

        Assert.IsType<InvalidDataException>(failure.InnerException);
        stream.Position = 0;
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task OpenAsync_FirstChunksMissing_IsRejectedAtOpen()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern(Batch * 2 * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        store.Chunks.Remove(MemoryFileContentChunkStore.Key(hash, 0));
        await using IndexedDbFileContentRepository repository = Repository(store);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_AfterDispose_Throws()
    {
        MemoryFileContentChunkStore store = new();
        byte[] original = IndexedDbFileContentRepositoryTests.Pattern(Batch * 2 * ChunkBytes);
        string hash = store.PutChunked(original, ChunkBytes);
        await using IndexedDbFileContentRepository repository = Repository(store);
        ChunkedContentReadStream stream = await ChunkedContentReadStream.OpenAsync(
            repository, hash, original.Length, CancellationToken.None);

        await stream.DisposeAsync();

        Assert.False(stream.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.ReadExactlyAsync(new byte[1]));
    }

    private static IndexedDbFileContentRepository Repository(MemoryFileContentChunkStore store) =>
        new(new IndexedDbFileContentRepositoryTests.ModuleRuntime(), chunks: store);
}
