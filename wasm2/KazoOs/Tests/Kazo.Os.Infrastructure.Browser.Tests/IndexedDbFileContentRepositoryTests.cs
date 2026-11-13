using System.Security.Cryptography;
using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// Verifies how file content larger than one transaction is written and read in batches (ADR 0245).
/// </summary>
public sealed class IndexedDbFileContentRepositoryTests
{
    private const int ChunkBytes = 256 * 1024;

    [Fact]
    public async Task WriteAsync_FileOfOneBatch_UsesOneTransaction()
    {
        ChunkStoreModule module = new();
        await using IndexedDbFileContentRepository repository = new(new ModuleRuntime(module));

        await repository.WriteAsync(new MemoryStream(Pattern(FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes)));

        Assert.Equal(1, module.WriteTransactions);
        Assert.Equal(FileContentStoragePolicy.ChunksPerTransaction, module.Chunks.Count);
    }

    [Fact]
    public async Task WriteAsync_FileLargerThanOneBatch_IsWrittenInSeveralTransactions()
    {
        ChunkStoreModule module = new();
        await using IndexedDbFileContentRepository repository = new(new ModuleRuntime(module));
        int length = (2 * FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes) + 1;

        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(Pattern(length)));

        Assert.Equal(3, module.WriteTransactions);
        Assert.True(module.LargestTransactionOperations <= FileContentStoragePolicy.ChunksPerTransaction);
        Assert.Equal((2 * FileContentStoragePolicy.ChunksPerTransaction) + 1, module.Chunks.Count);
        Assert.Equal(length, write.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Pattern(length))).ToLowerInvariant(), write.ContentHash);
    }

    [Fact]
    public async Task ReadAsync_FileLargerThanOneBatch_ReadsBackTheSameBytesInBatches()
    {
        ChunkStoreModule module = new();
        await using IndexedDbFileContentRepository repository = new(new ModuleRuntime(module));
        byte[] original = Pattern((FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes) + 12345);
        IndexedDbFileContentWrite write = await repository.WriteAsync(new MemoryStream(original));

        await using Stream read = await repository.ReadAsync(write.ContentHash, write.Length);

        using MemoryStream copy = new();
        await read.CopyToAsync(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Equal(2, module.ReadTransactions);
        Assert.True(module.LargestTransactionOperations <= FileContentStoragePolicy.ChunksPerTransaction);
    }

    [Fact]
    public async Task ReadAsync_LargeFileWithAMissingChunk_IsRejected()
    {
        ChunkStoreModule module = new();
        await using IndexedDbFileContentRepository repository = new(new ModuleRuntime(module));
        IndexedDbFileContentWrite write = await repository.WriteAsync(
            new MemoryStream(Pattern((FileContentStoragePolicy.ChunksPerTransaction * ChunkBytes) + 1)));
        module.Chunks.Remove($"{write.ContentHash}/{FileContentStoragePolicy.ChunksPerTransaction}");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ReadAsync(write.ContentHash, write.Length));
    }

    [Fact]
    public async Task WriteAsync_FileOfExactlyTheLimit_IsAcceptedAndOneMoreByteIsRefused()
    {
        ChunkStoreModule module = new();
        FileContentStoragePolicy policy = new(4 * ChunkBytes, ChunkBytes, "SHA-256", true, TimeSpan.FromDays(1));
        await using IndexedDbFileContentRepository repository = new(new ModuleRuntime(module), policy);

        await repository.WriteAsync(new MemoryStream(Pattern(4 * ChunkBytes)));
        await Assert.ThrowsAsync<FileContentTooLargeException>(
            async () => await repository.WriteAsync(new MemoryStream(Pattern((4 * ChunkBytes) + 1))));
    }

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * 31) ^ (index >> 8));
        }

        return bytes;
    }

    private sealed class ModuleRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    /// <summary>An in-memory <c>fsContent</c> store answering the operations the repository sends.</summary>
    private sealed class ChunkStoreModule : IJSObjectReference
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public Dictionary<string, JsonElement> Chunks { get; } = new(StringComparer.Ordinal);

        public int WriteTransactions { get; private set; }

        public int ReadTransactions { get; private set; }

        public int LargestTransactionOperations { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier != "executeTransaction")
            {
                return ValueTask.FromResult(default(TValue)!);
            }

            IReadOnlyList<IndexedDbOperation> operations = (IReadOnlyList<IndexedDbOperation>)args![4]!;
            LargestTransactionOperations = Math.Max(LargestTransactionOperations, operations.Count);
            if ((string)args[3]! == "readwrite")
            {
                WriteTransactions++;
            }
            else
            {
                ReadTransactions++;
            }

            JsonElement[] results = [.. operations.Select(Execute)];
            return ValueTask.FromResult((TValue)(object)results);
        }

        private JsonElement Execute(IndexedDbOperation operation)
        {
            switch (operation.Kind)
            {
                case "addIfAbsent":
                {
                    string key = Key(operation.Key);
                    bool added = Chunks.TryAdd(key, JsonSerializer.SerializeToElement(operation.Value, Web));
                    return JsonSerializer.SerializeToElement(new { added }, Web);
                }

                case "get":
                    return Chunks.TryGetValue(Key(operation.Key), out JsonElement chunk)
                        ? chunk
                        : JsonSerializer.SerializeToElement<object?>(null);
                case "getAll":
                    return JsonSerializer.SerializeToElement(
                        Chunks.Values
                            .Where(value => value.GetProperty("contentHash").GetString() == (string)operation.Query!)
                            .OrderBy(value => value.GetProperty("chunkIndex").GetInt32())
                            .ToArray());
                default:
                    throw new InvalidOperationException($"Unexpected operation {operation.Kind}.");
            }
        }

        private static string Key(object? key)
        {
            object[] parts = (object[])key!;
            return $"{parts[0]}/{parts[1]}";
        }
    }
}
