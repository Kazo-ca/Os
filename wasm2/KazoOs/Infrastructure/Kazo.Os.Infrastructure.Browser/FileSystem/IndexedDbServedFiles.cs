using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// The table of the addresses the service worker serves from the file system's content
/// (<c>servedFiles</c>, ADR 0274 §2): one row per <c>_content/&lt;assembly&gt;/&lt;path&gt;</c> of
/// an installed app, pointing at the content of the file in the app's folder.
/// </summary>
/// <remarks>
/// <para>
/// Serving a file writes no byte of it (ADR 0274 §3): the entry of the file is resolved, its
/// <c>contentHash</c> (SHA-256 of the whole content) is compared with the SHA-256 the manifest
/// declares, and the row is written in a transaction that first checks every chunk of the content
/// is present. The content served is therefore the one whose hash is in the manifest, as when the
/// bytes were read and checked before being copied to Cache Storage.
/// </para>
/// <para>
/// A row is a reference to its content: the orphan pass and the immediate reclaim of a system
/// rewrite keep a content a row still points at (ADR 0274 §4).
/// </para>
/// </remarks>
public sealed class IndexedDbServedFiles : IAsyncDisposable
{
    private const string Boundary = "ServedFilesWrite";

    /// <summary>Failure code of a row whose content chunks are not all present.</summary>
    internal const string ContentMissingFailureCode = "servedfiles.content-missing";

    private readonly IndexedDbInteropAdapter _adapter;
    private readonly IndexedDbFileSystemReader _reader;
    private readonly FileContentStoragePolicy _policy;

    /// <summary>Creates the table over the OS database.</summary>
    /// <param name="runtime">JavaScript runtime.</param>
    public IndexedDbServedFiles(IJSRuntime runtime)
        : this(runtime, null)
    {
    }

    internal IndexedDbServedFiles(IJSRuntime runtime, FileContentStoragePolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime);
        _reader = new IndexedDbFileSystemReader(runtime);
        _policy = policy ?? FileContentStoragePolicy.Default;
    }

    /// <summary>
    /// Serves each file of <paramref name="files"/> at its address, for <paramref name="appId"/>.
    /// A file that is missing, is not a regular file, or whose content does not have the declared
    /// SHA-256 is not served, and a row left at its address is removed (a stale copy must not be
    /// served instead).
    /// </summary>
    /// <param name="appId">The installed app the files belong to.</param>
    /// <param name="files">The files, as the manifest declares them.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    /// <returns>One result per file, in order.</returns>
    public async ValueTask<IReadOnlyList<ServedFileResult>> ServeAsync(
        string appId,
        IReadOnlyList<ServedFileRequest> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(files);
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        List<ServedFileResult> results = new(files.Count);
        List<string> refused = [];
        foreach (ServedFileRequest file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            ServedFileOutcome outcome = await ServeOneAsync(appId, file, cancellationToken).ConfigureAwait(false);
            if (outcome != ServedFileOutcome.Served)
            {
                refused.Add(file.Url);
            }

            results.Add(new ServedFileResult(file.Url, outcome));
        }

        if (refused.Count > 0)
        {
            await RemoveAsync(refused, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>Lists every served address, relative to the page's base.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The addresses, for example <c>_content/Kazo.Sample/x.js</c>.</returns>
    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("getAllKeys", KazoOsIndexedDbSchema.ServedFileStoreName)],
            cancellationToken).ConfigureAwait(false);
        JsonElement keys = results.Count == 1 ? results[0] : default;
        return keys.ValueKind == JsonValueKind.Array
            ? [.. keys.EnumerateArray().Where(key => key.ValueKind == JsonValueKind.String).Select(key => key.GetString()!)]
            : throw new InvalidDataException("Served file listing is malformed.");
    }

    /// <summary>Stops serving the given addresses, in one transaction.</summary>
    /// <param name="urls">The addresses.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public async ValueTask RemoveAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count == 0)
        {
            return;
        }

        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            [.. urls.Select(url => new IndexedDbOperation("delete", KazoOsIndexedDbSchema.ServedFileStoreName, Key: url))],
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ServedFileOutcome> ServeOneAsync(
        string appId,
        ServedFileRequest file,
        CancellationToken cancellationToken)
    {
        IndexedDbFileSystemEntryRecord? entry = await _reader.ResolveAsync(file.Path, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.Kind != (int)FileSystemEntryKind.File)
        {
            return ServedFileOutcome.Missing;
        }

        // A file created but never written has no content and no chunk to serve it from.
        if (entry.ContentHash is not { Length: > 0 } contentHash)
        {
            return ServedFileOutcome.Missing;
        }

        if (!string.Equals(contentHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return ServedFileOutcome.HashMismatch;
        }

        int chunkCount = entry.Length == 0 ? 1 : _policy.ChunkCountFor(entry.Length);
        ServedFileRecord row = new(
            file.Url, contentHash, entry.Length, file.ContentType, appId, checked((int)_policy.MaxChunkSizeBytes));
        try
        {
            await _adapter.ExecuteAsync(
                Boundary,
                IndexedDbTransactionMode.ReadWrite,
                [
                    // The row is written only when the content is whole: a reclaim between the
                    // resolution and this transaction leaves the address unserved, not broken.
                    new IndexedDbOperation(
                        "assertCount",
                        KazoOsIndexedDbSchema.FileContentStoreName,
                        IndexName: "contentHash",
                        Query: contentHash,
                        ExpectedValue: chunkCount,
                        FailureCode: ContentMissingFailureCode),
                    new IndexedDbOperation("put", KazoOsIndexedDbSchema.ServedFileStoreName, Value: row)
                ],
                cancellationToken).ConfigureAwait(false);
            return ServedFileOutcome.Served;
        }
        catch (JSException exception) when (exception.Message.Contains(ContentMissingFailureCode, StringComparison.Ordinal))
        {
            return ServedFileOutcome.ContentMissing;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _reader.DisposeAsync().ConfigureAwait(false);
        await _adapter.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>One file to serve.</summary>
/// <param name="Url">Its address relative to the page's base (<c>_content/Kazo.Sample/x.js</c>).</param>
/// <param name="Path">The file in the installed app's folder.</param>
/// <param name="Sha256">The SHA-256 its manifest declares, hexadecimal.</param>
/// <param name="ContentType">The <c>Content-Type</c> the service worker answers with.</param>
public sealed record ServedFileRequest(string Url, VirtualPath Path, string Sha256, string ContentType);

/// <summary>What became of one file to serve.</summary>
/// <param name="Url">Its address.</param>
/// <param name="Outcome">Served, or why not.</param>
public sealed record ServedFileResult(string Url, ServedFileOutcome Outcome);

/// <summary>Whether a file is served (ADR 0274 §3).</summary>
public enum ServedFileOutcome
{
    /// <summary>The address is served from the file's content.</summary>
    Served = 1,

    /// <summary>The file is missing, is not a regular file, or was never written.</summary>
    Missing = 2,

    /// <summary>The file's content is not the one the manifest declares.</summary>
    HashMismatch = 3,

    /// <summary>Chunks of the file's content are missing from the content store.</summary>
    ContentMissing = 4
}

/// <summary>One row of <c>servedFiles</c>, as the service worker reads it.</summary>
internal sealed record ServedFileRecord(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("appId")] string AppId,
    // The bytes per chunk the content was written with: the worker finds the chunks a Range covers.
    [property: JsonPropertyName("chunkSize")] int ChunkSize);
