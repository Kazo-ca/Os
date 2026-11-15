using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.Infrastructure.Browser.Interop;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// ADR 0272 §3: converts, once, the file content stored in base64 by a release before database
/// version 5 to the binary form, before the OS opens a session. The work is done by
/// <c>fileContent.js</c>: batches of at most <see cref="BatchBytes"/>, one transaction each, the
/// position recorded with each batch so a closed tab resumes there, one tab at a time.
/// </summary>
public static class FileContentConversion
{
    /// <summary>Largest batch of a conversion, in decoded bytes (16 MiB, ADR 0272 §3).</summary>
    public const int BatchBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Opens the OS database (upgrading it to the current version), then converts what is left
    /// to convert. Returns at once when the database was already converted.
    /// </summary>
    /// <param name="runtime">JavaScript runtime of the JSON adapter, which opens the database.</param>
    /// <param name="progress">
    /// Chunks looked at and chunks to look at; called before the first batch and after each one,
    /// never when there is nothing to convert.
    /// </param>
    /// <param name="waiting">Called when another tab is converting; this one waits for it.</param>
    /// <param name="cancellationToken">Cancels the opening of the database.</param>
    /// <returns>What the conversion did.</returns>
    [SupportedOSPlatform("browser")]
    public static async Task<FileContentConversionResult> RunAsync(
        IJSRuntime runtime,
        Action<long, long> progress,
        Action waiting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(waiting);
        await using (IndexedDbInteropAdapter adapter = new(runtime))
        {
            await adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(true);
        }

        string json = await BrowserFileContentChunkStore.ConvertLegacyContentAsync(
            BatchBytes,
            (looked, total) => progress((long)looked, (long)total),
            waiting).ConfigureAwait(true);
        return Parse(json);
    }

    /// <summary>Reads the JSON summary of <c>convertLegacyContent</c>.</summary>
    /// <param name="json">The summary.</param>
    internal static FileContentConversionResult Parse(string json)
    {
        FileContentConversionSummary summary = JsonSerializer.Deserialize(
                json,
                FileContentConversionJsonContext.Default.FileContentConversionSummary)
            ?? throw new InvalidDataException("The conversion summary is empty.");
        return new FileContentConversionResult(
            summary.AlreadyDone,
            summary.Chunks,
            summary.Converted,
            summary.Bytes,
            [.. (summary.Damaged ?? []).Where(item => item.Length == 2)
                .Select(item => new FileContentChunkKey(item[0].GetString() ?? string.Empty, item[1].GetInt32()))]);
    }
}

/// <summary>What one run of <see cref="FileContentConversion"/> did.</summary>
/// <param name="AlreadyDone">The database had been converted before; nothing was read.</param>
/// <param name="Chunks">Chunks looked at, in this run and the runs it resumed.</param>
/// <param name="Converted">Chunks converted from base64 to the binary form.</param>
/// <param name="Bytes">Bytes of the converted chunks.</param>
/// <param name="Damaged">Chunks of this run left as they were because their base64 does not decode.</param>
public sealed record FileContentConversionResult(
    bool AlreadyDone,
    long Chunks,
    long Converted,
    long Bytes,
    IReadOnlyList<FileContentChunkKey> Damaged);

/// <summary>The key of one content chunk: the SHA-256 of its file and its number.</summary>
/// <param name="ContentHash">SHA-256 of the whole file.</param>
/// <param name="ChunkIndex">Zero-based number of the chunk.</param>
public sealed record FileContentChunkKey(string ContentHash, int ChunkIndex);

/// <summary>JSON shape returned by <c>convertLegacyContent</c>.</summary>
internal sealed record FileContentConversionSummary(
    bool AlreadyDone,
    long Chunks,
    long Converted,
    long Bytes,
    JsonElement[][]? Damaged);

/// <summary>Trim-safe JSON of <see cref="FileContentConversionSummary"/>.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FileContentConversionSummary))]
internal sealed partial class FileContentConversionJsonContext : JsonSerializerContext;
