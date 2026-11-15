using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Backup;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies versioned exports and explicit atomic restore semantics.</summary>
public sealed class IndexedDbBackupServiceTests
{
    [Fact]
    public async Task Export_and_replace_use_all_store_snapshot_then_clear_and_put_atomically()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));

        string backup = await service.ExportAsync();
        IndexedDbRestoreResult result = await service.RestoreAsync(backup, IndexedDbRestoreMode.Replace);

        using JsonDocument document = JsonDocument.Parse(backup);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(64, document.RootElement.GetProperty("sha256").GetString()!.Length);
        Assert.Equal(1, result.RecordCount);
        TransactionInvocation export = module.Transactions[0];
        Assert.Equal("readonly", export.Mode);
        Assert.Equal(KazoOsIndexedDbSchema.ObjectStores.Count, export.Operations.Count);
        Assert.All(export.Operations, operation => Assert.Equal("getAll", operation.Kind));
        TransactionInvocation restore = module.Transactions[1];
        Assert.Equal("readwrite", restore.Mode);
        Assert.Equal(KazoOsIndexedDbSchema.ObjectStores.Count, restore.Operations.Count(operation => operation.Kind == "clear"));
        Assert.Single(restore.Operations, operation => operation.Kind == "put");
    }

    [Fact]
    public async Task Restore_rejects_tampered_backup_before_write_interop()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));
        string backup = await service.ExportAsync();
        string tampered = backup.Replace("user-one", "user-two", StringComparison.Ordinal);

        await Assert.ThrowsAsync<IndexedDbBackupValidationException>(async () =>
            await service.RestoreAsync(tampered, IndexedDbRestoreMode.Merge));

        Assert.Single(module.Transactions);
        Assert.Equal("readonly", module.Transactions[0].Mode);
    }

    [Fact]
    public async Task Merge_puts_archive_records_over_existing_ones_without_clearing()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));
        string backup = await service.ExportAsync();

        IndexedDbRestoreResult result = await service.RestoreAsync(backup, IndexedDbRestoreMode.Merge);

        TransactionInvocation restore = module.Transactions[1];
        IndexedDbOperation operation = Assert.Single(restore.Operations);
        Assert.Equal("put", operation.Kind);
        Assert.Equal(IndexedDbRestoreMode.Merge, result.Mode);
    }

    [Fact]
    public async Task Archive_round_trips_through_gzip_and_plain_json_is_still_accepted()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));

        byte[] archive = await service.ExportArchiveAsync();
        string json = IndexedDbBackupService.ReadArchive(archive);

        Assert.Equal(0x1F, archive[0]);
        Assert.Equal(0x8B, archive[1]);
        IndexedDbBackupSummary summary = IndexedDbBackupService.Inspect(json);
        Assert.Equal(1, summary.RecordCount);
        Assert.Equal(1, summary.StoreRecordCounts[KazoOsIndexedDbSchema.UserStoreName]);
        Assert.Equal(json, IndexedDbBackupService.ReadArchive(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Damaged_gzip_archive_is_refused()
    {
        byte[] damaged = [0x1F, 0x8B, 0x08, 0x00, 0x01, 0x02, 0x03];

        // A truncated stream either fails to decompress or yields no backup: both are refused.
        Assert.Throws<IndexedDbBackupValidationException>(
            () => IndexedDbBackupService.Inspect(IndexedDbBackupService.ReadArchive(damaged)));
    }

    [Fact]
    public async Task Backup_of_another_schema_version_is_refused_with_both_versions_named()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));
        string backup = await service.ExportAsync();
        string older = backup.Replace(
            $"\"databaseSchemaVersion\":{KazoOsIndexedDbSchema.BackupSchemaVersion}",
            "\"databaseSchemaVersion\":3",
            StringComparison.Ordinal);

        IndexedDbBackupValidationException exception = Assert.Throws<IndexedDbBackupValidationException>(
            () => IndexedDbBackupService.Inspect(older));

        Assert.Contains("version 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"version {KazoOsIndexedDbSchema.BackupSchemaVersion}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_still_names_schema_version_4_so_a_previous_release_restores_it()
    {
        // ADR 0272 §5: database version 5 kept the stores and records of a backup.
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));

        using JsonDocument document = JsonDocument.Parse(await service.ExportAsync());

        Assert.Equal(5, KazoOsIndexedDbSchema.CurrentVersion);
        Assert.Equal(4, document.RootElement.GetProperty("databaseSchemaVersion").GetInt32());
    }

    [Fact]
    public async Task Restore_asks_for_content_chunks_in_their_binary_form()
    {
        JsonElement[] snapshot = CreateSnapshot();
        int contentIndex = KazoOsIndexedDbSchema.ObjectStores
            .Select((store, index) => (store, index))
            .Single(item => item.store.Name == KazoOsIndexedDbSchema.FileContentStoreName).index;
        // A chunk as indexedDb.js hands it to C#: in its JSON form, base64 with its hash.
        snapshot[contentIndex] = JsonDocument.Parse(
            "[{\"contentHash\":\"abc\",\"chunkIndex\":0,\"dataBase64\":\"aGVsbG8=\",\"chunkHash\":\"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824\",\"createdUtcMs\":1}]")
            .RootElement.Clone();
        FakeIndexedDbModule module = new(snapshot);
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));
        string backup = await service.ExportAsync();

        await service.RestoreAsync(backup, IndexedDbRestoreMode.Merge);

        IndexedDbOperation chunk = Assert.Single(
            module.Transactions[^1].Operations,
            operation => operation.ObjectStoreName == KazoOsIndexedDbSchema.FileContentStoreName);
        Assert.True(chunk.BinaryChunk);
        Assert.All(
            module.Transactions[^1].Operations.Where(operation => operation.ObjectStoreName != KazoOsIndexedDbSchema.FileContentStoreName),
            operation => Assert.Null(operation.BinaryChunk));
    }

    private static JsonElement[] CreateSnapshot()
    {
        JsonElement empty = JsonDocument.Parse("[]").RootElement.Clone();
        JsonElement[] stores = Enumerable.Repeat(empty, KazoOsIndexedDbSchema.ObjectStores.Count).ToArray();
        int userIndex = KazoOsIndexedDbSchema.ObjectStores
            .Select((store, index) => (store, index))
            .Single(item => item.store.Name == KazoOsIndexedDbSchema.UserStoreName)
            .index;
        stores[userIndex] = JsonDocument.Parse("[{\"id\":\"user-one\",\"loginName\":\"alice\"}]")
            .RootElement.Clone();
        return stores;
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    private sealed class FakeIndexedDbModule(JsonElement[] snapshot) : IJSObjectReference
    {
        internal List<TransactionInvocation> Transactions { get; } = [];

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

            object?[] arguments = args ?? [];
            TransactionInvocation invocation = new(
                Assert.IsType<string>(arguments[3]),
                Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(arguments[4]));
            Transactions.Add(invocation);
            return ValueTask.FromResult((TValue)(object)(invocation.Mode == "readonly" ? snapshot : []));
        }
    }

    private sealed record TransactionInvocation(
        string Mode,
        IReadOnlyList<IndexedDbOperation> Operations);
}