using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies bounded and race-safe orphan content collection.</summary>
public sealed class IndexedDbFileSystemMaintenanceTests
{
    [Fact]
    public async Task CleanupAsync_DeletesOldOrphanButRetainsConcurrentlyReferencedHash()
    {
        ScriptedModule module = new(
            One("{\"candidates\":[\"orphan\",\"used\"],\"scannedValues\":3,\"unreferenced\":2}"),
            [Parse("{\"deleted\":1,\"referenced\":false}"), Parse("{\"deleted\":0,\"referenced\":true}")]);
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        IndexedDbFileContentCleanupResult result = await maintenance.CleanupAsync();

        Assert.Equal(2, result.CandidateHashes);
        Assert.Equal(1, result.DeletedChunks);
        Assert.Equal(1, result.RetainedReferencedHashes);
        IReadOnlyList<IndexedDbOperation> operations =
            Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(module.ExecuteArguments[1][4]);
        Assert.All(operations, operation =>
        {
            Assert.Equal("deleteAllByIndexIfUnreferenced", operation.Kind);
            Assert.Equal(KazoOsIndexedDbSchema.FileSystemEntryStoreName, operation.ReferenceObjectStoreName);
            Assert.Equal("contentHash", operation.ReferenceIndexName);
            Assert.Equal("createdUtcMs", operation.CompareProperty);
            Assert.Equal(CutoffMs(FileContentStoragePolicy.DefaultOrphanRetention), operation.ExpectedValue);
        });
    }

    [Fact]
    public async Task Discovery_reads_keys_and_references_in_javascript_not_the_whole_content_store()
    {
        ScriptedModule module = new(One("{\"candidates\":[],\"scannedValues\":0,\"unreferenced\":0}"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        IndexedDbFileContentCleanupResult result = await maintenance.InitializeAsync();

        Assert.Equal(new IndexedDbFileContentCleanupResult(0, 0, 0), result);
        IndexedDbOperation discovery = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(Assert.Single(module.ExecuteArguments)[4]));
        Assert.Equal("findUnreferencedOlderThan", discovery.Kind);
        Assert.Equal(KazoOsIndexedDbSchema.FileContentStoreName, discovery.ObjectStoreName);
        Assert.Equal(KazoOsIndexedDbSchema.FileSystemEntryStoreName, discovery.ReferenceObjectStoreName);
        Assert.Equal(IndexedDbFileSystemMaintenance.HashesPerPass, discovery.Count);
        Assert.Equal(CutoffMs(FileContentStoragePolicy.DefaultOrphanRetention), discovery.ExpectedValue);
    }

    [Fact]
    public async Task ReclaimAsync_with_low_space_uses_the_short_retention()
    {
        ScriptedModule module = new(One("{\"candidates\":[],\"scannedValues\":0,\"unreferenced\":0}"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        long deleted = await maintenance.ReclaimAsync(lowSpace: true);

        Assert.Equal(0, deleted);
        IndexedDbOperation discovery = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(Assert.Single(module.ExecuteArguments)[4]));
        Assert.Equal(CutoffMs(FileContentStoragePolicy.DefaultLowSpaceOrphanRetention), discovery.ExpectedValue);
    }

    [Fact]
    public async Task ReclaimAsync_runs_passes_until_one_is_not_full()
    {
        string full = "{\"candidates\":[" + string.Join(
            ",",
            Enumerable.Range(0, IndexedDbFileSystemMaintenance.HashesPerPass).Select(index => $"\"h{index}\"")) + "]}";
        JsonElement[] deletions = [.. Enumerable.Range(0, IndexedDbFileSystemMaintenance.HashesPerPass)
            .Select(_ => Parse("{\"deleted\":2,\"referenced\":false}"))];
        ScriptedModule module = new(
            One(full),
            deletions,
            One("{\"candidates\":[\"last\"]}"),
            [Parse("{\"deleted\":1,\"referenced\":false}")]);
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        long deleted = await maintenance.ReclaimAsync(lowSpace: false);

        Assert.Equal((IndexedDbFileSystemMaintenance.HashesPerPass * 2) + 1, deleted);
        Assert.Equal(4, module.ExecuteArguments.Count);
    }

    [Fact]
    public void The_low_space_retention_is_never_longer_than_the_normal_one()
    {
        FileContentStoragePolicy policy = new(
            FileContentStoragePolicy.DefaultMaxFileSizeBytes,
            FileContentStoragePolicy.DefaultMaxChunkSizeBytes,
            FileContentStoragePolicy.DefaultContentHashAlgorithm,
            deduplicateChunks: true,
            orphanRetention: TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(1), policy.LowSpaceOrphanRetention);
        Assert.Equal(TimeSpan.FromDays(1), FileContentStoragePolicy.Default.OrphanRetention);
        Assert.Equal(TimeSpan.FromMinutes(5), FileContentStoragePolicy.Default.LowSpaceOrphanRetention);
        Assert.Throws<ArgumentOutOfRangeException>(() => policy with { LowSpaceOrphanRetention = TimeSpan.Zero });
    }

    private static long CutoffMs(TimeSpan retention) =>
        (new FixedTimeProvider().GetUtcNow() - retention).ToUnixTimeMilliseconds();

    private static JsonElement[] One(string json) => [Parse(json)];

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    private sealed class ScriptedModule(params JsonElement[][] results) : IJSObjectReference
    {
        private readonly Queue<JsonElement[]> _results = new(results);

        public List<object?[]> ExecuteArguments { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "executeTransaction")
            {
                ExecuteArguments.Add(args ?? []);
                return ValueTask.FromResult((TValue)(object)_results.Dequeue());
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    }
}