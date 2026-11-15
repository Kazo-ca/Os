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
        ScriptedModule module = new(Parse(
            "{\"candidates\":[\"orphan\",\"used\"],\"results\":[{\"deleted\":1,\"referenced\":false},{\"deleted\":0,\"referenced\":true}]}"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        IndexedDbFileContentCleanupResult result = await maintenance.CleanupAsync();

        Assert.Equal(2, result.CandidateHashes);
        Assert.Equal(1, result.DeletedChunks);
        Assert.Equal(1, result.RetainedReferencedHashes);
        object?[] pass = Assert.Single(module.PassArguments);
        Assert.Equal("kazoos-orphan-content-pass", pass[3]);
        IndexedDbOperation delete = Assert.IsType<IndexedDbOperation>(pass[5]);
        Assert.Equal("deleteAllByIndexIfUnreferenced", delete.Kind);
        Assert.Equal(KazoOsIndexedDbSchema.FileSystemEntryStoreName, delete.ReferenceObjectStoreName);
        Assert.Equal("contentHash", delete.ReferenceIndexName);
        // ADR 0274 §4: an address the service worker serves keeps its content too.
        Assert.Equal(KazoOsIndexedDbSchema.ServedFileStoreName, delete.ExtraReferenceObjectStoreName);
        Assert.Equal("contentHash", delete.ExtraReferenceIndexName);
        Assert.Equal("createdUtcMs", delete.CompareProperty);
        Assert.Equal(CutoffMs(FileContentStoragePolicy.DefaultOrphanRetention), delete.ExpectedValue);
    }

    [Fact]
    public async Task Discovery_reads_keys_and_references_in_javascript_not_the_whole_content_store()
    {
        ScriptedModule module = new(Parse("{\"candidates\":[],\"results\":[]}"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        IndexedDbFileContentCleanupResult result = await maintenance.InitializeAsync();

        Assert.Equal(new IndexedDbFileContentCleanupResult(0, 0, 0), result);
        Assert.Empty(module.ExecuteArguments);
        IndexedDbOperation discovery = Assert.IsType<IndexedDbOperation>(Assert.Single(module.PassArguments)[4]);
        Assert.Equal("findUnreferencedOlderThan", discovery.Kind);
        Assert.Equal(KazoOsIndexedDbSchema.FileContentStoreName, discovery.ObjectStoreName);
        Assert.Equal(KazoOsIndexedDbSchema.FileSystemEntryStoreName, discovery.ReferenceObjectStoreName);
        Assert.Equal(KazoOsIndexedDbSchema.ServedFileStoreName, discovery.ExtraReferenceObjectStoreName);
        Assert.Equal(IndexedDbFileSystemMaintenance.HashesPerPass, discovery.Count);
        Assert.Equal(CutoffMs(FileContentStoragePolicy.DefaultOrphanRetention), discovery.ExpectedValue);
    }

    [Fact]
    public async Task A_pass_skipped_by_a_hidden_page_or_another_tab_deletes_nothing()
    {
        ScriptedModule module = new(Parse("{\"skipped\":\"locked\"}"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        Assert.Equal(0, await maintenance.ReclaimAsync(lowSpace: false));
        Assert.Single(module.PassArguments);
    }

    [Fact]
    public async Task ReclaimAsync_with_low_space_uses_the_short_retention()
    {
        ScriptedModule module = new(Parse("{\"candidates\":[],\"results\":[]}"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        long deleted = await maintenance.ReclaimAsync(lowSpace: true);

        Assert.Equal(0, deleted);
        IndexedDbOperation discovery = Assert.IsType<IndexedDbOperation>(Assert.Single(module.PassArguments)[4]);
        Assert.Equal(CutoffMs(FileContentStoragePolicy.DefaultLowSpaceOrphanRetention), discovery.ExpectedValue);
    }

    [Fact]
    public async Task ReclaimAsync_runs_passes_until_one_is_not_full()
    {
        string candidates = string.Join(
            ",",
            Enumerable.Range(0, IndexedDbFileSystemMaintenance.HashesPerPass).Select(index => $"\"h{index}\""));
        string results = string.Join(
            ",",
            Enumerable.Range(0, IndexedDbFileSystemMaintenance.HashesPerPass).Select(_ => "{\"deleted\":2}"));
        ScriptedModule module = new(
            Parse($"{{\"candidates\":[{candidates}],\"results\":[{results}]}}"),
            Parse("{\"candidates\":[\"last\"],\"results\":[{\"deleted\":1}]}"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        long deleted = await maintenance.ReclaimAsync(lowSpace: false);

        Assert.Equal((IndexedDbFileSystemMaintenance.HashesPerPass * 2) + 1, deleted);
        Assert.Equal(2, module.PassArguments.Count);
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

    private sealed class ScriptedModule(params JsonElement[] passes) : IJSObjectReference
    {
        private readonly Queue<JsonElement> _passes = new(passes);

        public List<object?[]> ExecuteArguments { get; } = [];

        public List<object?[]> PassArguments { get; } = [];

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
                throw new InvalidOperationException("The orphan pass must run through runOrphanPass.");
            }

            if (identifier == "runOrphanPass")
            {
                PassArguments.Add(args ?? []);
                return ValueTask.FromResult((TValue)(object)_passes.Dequeue());
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