using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Infrastructure.Browser.Storage;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// <see cref="IFileContentChunkStore"/> over <c>wwwroot/fileContent.js</c>, imported with
/// <c>[JSImport]</c> (ADR 0272 §2): the bytes cross as a <c>MemoryView</c> on the .NET heap, and
/// WebCrypto hashes them. The module is imported by this class itself; no host references it.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class BrowserFileContentChunkStore : IFileContentChunkStore
{
    private const string ModuleName = "kazoos-file-content";
    private const string ModulePath = "_content/Kazo.Os.Infrastructure.Browser/fileContent.js";
    private static Task? _import;

    /// <summary>Gets the one instance: the module and its connection are per page.</summary>
    internal static BrowserFileContentChunkStore Instance { get; } = new();

    private BrowserFileContentChunkStore()
    {
    }

    /// <inheritdoc />
    public async ValueTask<string> HashAsync(ArraySegment<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ImportAsync().ConfigureAwait(true);
        return await Sha256(bytes).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async ValueTask<int> WriteChunksAsync(
        string contentHash,
        int firstIndex,
        ArraySegment<byte> bytes,
        int chunkSize,
        long createdUtcMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ImportAsync().ConfigureAwait(true);
        try
        {
            return await WriteChunks(
                KazoOsIndexedDbSchema.DatabaseName,
                KazoOsIndexedDbSchema.CurrentVersion,
                contentHash,
                firstIndex,
                bytes,
                chunkSize,
                createdUtcMs).ConfigureAwait(true);
        }
        catch (JSException exception) when (exception.Message.Contains("QuotaExceededError", StringComparison.Ordinal))
        {
            // Same contract as the JSON adapter: a full storage aborts the whole transaction (ADR 0099).
            throw new BrowserStorageQuotaException(
                "Browser storage quota was exhausted; the file content transaction was not committed.",
                exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask ReadChunksAsync(
        string contentHash,
        int firstIndex,
        int count,
        ArraySegment<byte> destination,
        int chunkSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ImportAsync().ConfigureAwait(true);
        string problem = await ReadChunks(
            KazoOsIndexedDbSchema.DatabaseName,
            KazoOsIndexedDbSchema.CurrentVersion,
            contentHash,
            firstIndex,
            count,
            destination,
            chunkSize).ConfigureAwait(true);
        if (problem.Length > 0)
        {
            throw new InvalidDataException($"Persisted file content is damaged: {problem}.");
        }
    }

    /// <summary>
    /// Converts the chunks still stored in base64 to the binary form (ADR 0272 §3); see
    /// <c>convertLegacyContent</c> in <c>fileContent.js</c>.
    /// </summary>
    /// <param name="batchBytes">Largest batch, in decoded bytes (one transaction each).</param>
    /// <param name="progress">Chunks looked at and chunks to look at.</param>
    /// <param name="waiting">Called when another tab is converting.</param>
    /// <returns>The JSON summary of the conversion.</returns>
    internal static async Task<string> ConvertLegacyContentAsync(
        int batchBytes,
        Action<double, double> progress,
        Action waiting)
    {
        await ImportAsync().ConfigureAwait(true);
        return await ConvertLegacyContent(
            KazoOsIndexedDbSchema.DatabaseName,
            KazoOsIndexedDbSchema.CurrentVersion,
            batchBytes,
            progress,
            waiting).ConfigureAwait(true);
    }

    private static Task ImportAsync() => _import ??= ImportCoreAsync();

    private static async Task ImportCoreAsync()
    {
        string baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")?.GetPropertyAsString("baseURI") ?? "/";
        await JSHost.ImportAsync(ModuleName, new Uri(new Uri(baseUri), ModulePath).ToString()).ConfigureAwait(true);
    }

    [JSImport("sha256", ModuleName)]
    private static partial Task<string> Sha256([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes);

    [JSImport("writeChunks", ModuleName)]
    private static partial Task<int> WriteChunks(
        string databaseName,
        int version,
        string contentHash,
        int firstIndex,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes,
        int chunkSize,
        double createdUtcMs);

    [JSImport("readChunks", ModuleName)]
    private static partial Task<string> ReadChunks(
        string databaseName,
        int version,
        string contentHash,
        int firstIndex,
        int count,
        [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> destination,
        int chunkSize);

    [JSImport("convertLegacyContent", ModuleName)]
    private static partial Task<string> ConvertLegacyContent(
        string databaseName,
        int version,
        int batchBytes,
        [JSMarshalAs<JSType.Function<JSType.Number, JSType.Number>>] Action<double, double> progress,
        [JSMarshalAs<JSType.Function>] Action waiting);
}
