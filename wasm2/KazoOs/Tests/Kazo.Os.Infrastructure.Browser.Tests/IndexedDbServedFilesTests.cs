using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// Verifies the <c>servedFiles</c> table (ADR 0274 §3): an address is served from the content of the
/// installed app's file only when that content is the one the manifest declares and is whole, and
/// no byte of it is read or copied.
/// </summary>
public sealed class IndexedDbServedFilesTests
{
    private const string AppId = "org.kazoos.sample";
    private const string Url = "_content/Kazo.Sample/app.js";
    private const string Hash = "aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11";

    private static readonly VirtualPath FilePath = VirtualPath.Parse("/apps/sample/wwwroot/app.js");

    [Fact]
    public async Task ServeAsync_WritesARowPointingAtTheContentAfterCheckingItsChunks()
    {
        // The file is 10 bytes with chunks of 4: three chunks must be present.
        ScriptedModule module = new(Resolved(File(Hash, length: 10)), Result("[3,null]"));
        await using IndexedDbServedFiles table = new(
            new FakeJsRuntime(module),
            new FileContentStoragePolicy(1024, 4, "SHA-256", true, TimeSpan.FromDays(1)));

        IReadOnlyList<ServedFileResult> results = await table.ServeAsync(AppId, [Request(Hash.ToUpperInvariant())]);

        Assert.Equal([new ServedFileResult(Url, ServedFileOutcome.Served)], results);
        // The ServedFilesWrite boundary: the row and the chunks it counts, in one transaction.
        Assert.Equal(
            [KazoOsIndexedDbSchema.ServedFileStoreName, KazoOsIndexedDbSchema.FileContentStoreName],
            Assert.IsAssignableFrom<IEnumerable<string>>(module.Executions[1].Arguments[2]));
        IReadOnlyList<IndexedDbOperation> write = Operations(module.Executions[1]);
        Assert.Equal("assertCount", write[0].Kind);
        Assert.Equal(KazoOsIndexedDbSchema.FileContentStoreName, write[0].ObjectStoreName);
        Assert.Equal("contentHash", write[0].IndexName);
        Assert.Equal(Hash, write[0].Query);
        Assert.Equal(3, write[0].ExpectedValue);
        Assert.Equal("put", write[1].Kind);
        Assert.Equal(KazoOsIndexedDbSchema.ServedFileStoreName, write[1].ObjectStoreName);
        using JsonDocument row = JsonDocument.Parse(JsonSerializer.Serialize(write[1].Value));
        Assert.Equal(Url, row.RootElement.GetProperty("url").GetString());
        Assert.Equal(Hash, row.RootElement.GetProperty("contentHash").GetString());
        Assert.Equal(10, row.RootElement.GetProperty("length").GetInt64());
        Assert.Equal("text/javascript", row.RootElement.GetProperty("contentType").GetString());
        Assert.Equal(AppId, row.RootElement.GetProperty("appId").GetString());
        Assert.Equal(4, row.RootElement.GetProperty("chunkSize").GetInt32());
        // No byte of the file is read: the resolution and this write are the only transactions.
        Assert.Equal(2, module.Executions.Count);
    }

    [Fact]
    public async Task ServeAsync_EmptyFileStillNeedsItsOneChunk()
    {
        ScriptedModule module = new(Resolved(File(Hash, length: 0)), Result("[1,null]"));
        await using IndexedDbServedFiles table = new(new FakeJsRuntime(module));

        await table.ServeAsync(AppId, [Request(Hash)]);

        Assert.Equal(1, Operations(module.Executions[1])[0].ExpectedValue);
    }

    [Fact]
    public async Task ServeAsync_ContentOtherThanTheManifestsIsNotServedAndItsOldRowIsRemoved()
    {
        ScriptedModule module = new(Resolved(File(new string('b', 64), length: 10)), Result("[null]"));
        await using IndexedDbServedFiles table = new(new FakeJsRuntime(module));

        IReadOnlyList<ServedFileResult> results = await table.ServeAsync(AppId, [Request(Hash)]);

        Assert.Equal(ServedFileOutcome.HashMismatch, Assert.Single(results).Outcome);
        IndexedDbOperation removal = Assert.Single(Operations(module.Executions[1]));
        Assert.Equal("delete", removal.Kind);
        Assert.Equal(KazoOsIndexedDbSchema.ServedFileStoreName, removal.ObjectStoreName);
        Assert.Equal(Url, removal.Key);
    }

    [Fact]
    public async Task ServeAsync_MissingDirectoryOrNeverWrittenFileIsNotServed()
    {
        ScriptedModule module = new(
            Result("[[]]"),
            Resolved(File(contentHash: null, length: 0)),
            Resolved(File(Hash, length: 1, FileSystemEntryKind.Directory)),
            Result("[null,null,null]"));
        await using IndexedDbServedFiles table = new(new FakeJsRuntime(module));

        IReadOnlyList<ServedFileResult> results = await table.ServeAsync(
            AppId,
            [Request(Hash), Request(Hash) with { Url = "_content/Kazo.Sample/b.js" }, Request(Hash) with { Url = "_content/Kazo.Sample/c" }]);

        Assert.All(results, result => Assert.Equal(ServedFileOutcome.Missing, result.Outcome));
        Assert.Equal(3, Operations(module.Executions[3]).Count);
    }

    [Fact]
    public async Task ServeAsync_ContentReclaimedBeforeTheWriteLeavesTheAddressUnserved()
    {
        ScriptedModule module = new(
            Resolved(File(Hash, length: 10)),
            Throw(new JSException($"{IndexedDbServedFiles.ContentMissingFailureCode}: 2 of 3 chunks")),
            Result("[null]"));
        await using IndexedDbServedFiles table = new(new FakeJsRuntime(module));

        IReadOnlyList<ServedFileResult> results = await table.ServeAsync(AppId, [Request(Hash)]);

        Assert.Equal(ServedFileOutcome.ContentMissing, Assert.Single(results).Outcome);
        Assert.Equal("delete", Assert.Single(Operations(module.Executions[2])).Kind);
    }

    [Fact]
    public async Task ServeAsync_OtherInteropFailuresAreNotTakenForAMissingContent()
    {
        ScriptedModule module = new(Resolved(File(Hash, length: 10)), Throw(new JSException("AbortError: the transaction was aborted")));
        await using IndexedDbServedFiles table = new(new FakeJsRuntime(module));

        await Assert.ThrowsAsync<JSException>(async () => await table.ServeAsync(AppId, [Request(Hash)]));
    }

    [Fact]
    public async Task ListAsync_ReturnsTheServedAddresses()
    {
        ScriptedModule module = new(Result($"[[\"{Url}\",\"_content/Kazo.Sample/b.css\"]]"));
        await using IndexedDbServedFiles table = new(new FakeJsRuntime(module));

        IReadOnlyList<string> addresses = await table.ListAsync();

        Assert.Equal([Url, "_content/Kazo.Sample/b.css"], addresses);
        Assert.Equal("getAllKeys", Assert.Single(Operations(module.Executions[0])).Kind);
    }

    [Fact]
    public async Task RemoveAsync_DeletesEveryAddressInOneTransactionAndNothingForNone()
    {
        ScriptedModule module = new(Result("[null,null]"));
        await using IndexedDbServedFiles table = new(new FakeJsRuntime(module));

        await table.RemoveAsync([]);
        await table.RemoveAsync(["a", "b"]);

        Assert.Equal(["a", "b"], Operations(Assert.Single(module.Executions)).Select(operation => operation.Key));
    }

    private static ServedFileRequest Request(string sha256) => new(Url, FilePath, sha256, "text/javascript");

    private static IReadOnlyList<IndexedDbOperation> Operations(Invocation invocation) =>
        Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(invocation.Arguments[4]);

    /// <summary>A transaction's results: a JSON array with one element per operation.</summary>
    private static Func<JsonElement[]> Result(string json)
    {
        JsonElement[] elements = [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(element => element.Clone())];
        return () => elements;
    }

    private static Func<JsonElement[]> Throw(Exception exception) => () => throw exception;

    /// <summary>The resolution of <see cref="FilePath"/>: its four components, the last one the file.</summary>
    private static Func<JsonElement[]> Resolved(string file) =>
        Result($"[[{Directory('1')},{Directory('2')},{Directory('3')},{file}]]");

    private static string Directory(char digit) => File(null, 0, FileSystemEntryKind.Directory, new string(digit, 32));

    private static string File(
        string? contentHash,
        long length,
        FileSystemEntryKind kind = FileSystemEntryKind.File,
        string id = "99999999999999999999999999999999") => $$"""
        {
          "id":"{{id}}",
          "kind":{{(int)kind}},
          "ownerId":"system",
          "groupId":"system",
          "permissionsMode":420,
          "createdUtcMs":1785664800000,
          "contentModifiedUtcMs":1785664800000,
          "metadataChangedUtcMs":1785664800000,
          "revision":1,
          "length":{{length}},
          "symbolicLinkTarget":null,
          "contentHash":{{(contentHash is null ? "null" : $"\"{contentHash}\"")}},
          "contentKind":1,
          "mediaType":"text/javascript",
          "encodingName":null
        }
        """;

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    /// <summary>Answers each transaction with the next scripted result, or throws it.</summary>
    private sealed class ScriptedModule(params Func<JsonElement[]>[] results) : IJSObjectReference
    {
        private readonly Queue<Func<JsonElement[]>> _results = new(results);

        public List<Invocation> Executions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "executeTransaction")
            {
                return ValueTask.FromResult(default(TValue)!);
            }

            Executions.Add(new Invocation(args ?? []));
            return ValueTask.FromResult((TValue)(object)_results.Dequeue()());
        }
    }

    private sealed record Invocation(object?[] Arguments);
}
