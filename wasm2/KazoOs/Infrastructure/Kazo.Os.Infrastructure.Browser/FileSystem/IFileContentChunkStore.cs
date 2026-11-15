namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// The binary side of the content repository (ADR 0272): stores and reads the chunks of a file in
/// the <c>fsContent</c> store as bytes, with the SHA-256 of each chunk, without JSON or base64.
/// In the browser it is <see cref="BrowserFileContentChunkStore"/> (<c>fileContent.js</c>, imported
/// with <c>[JSImport]</c>); tests give an in-memory one.
/// </summary>
/// <remarks>
/// Every call is one IndexedDB transaction at most, so the caller can count it in
/// <c>/proc/diskstats</c> (ADR 0057 §4). The database is opened, and upgraded when needed, by the
/// JSON adapter before the first call.
/// </remarks>
internal interface IFileContentChunkStore
{
    /// <summary>Computes the SHA-256 of <paramref name="bytes"/>, in lowercase hexadecimal.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="cancellationToken">Observed before the call starts.</param>
    ValueTask<string> HashAsync(ArraySegment<byte> bytes, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the chunks of <paramref name="bytes"/>, numbered from <paramref name="firstIndex"/>,
    /// in one transaction; a chunk already stored at its key is left as it is.
    /// </summary>
    /// <param name="contentHash">SHA-256 of the whole file, the first part of every key.</param>
    /// <param name="firstIndex">Number of the first chunk.</param>
    /// <param name="bytes">The bytes of the chunks; an empty segment stores one empty chunk.</param>
    /// <param name="chunkSize">Bytes per chunk; only the last chunk of the file may be shorter.</param>
    /// <param name="createdUtcMs">Creation time stamped on the new chunks.</param>
    /// <param name="cancellationToken">Observed before the call starts.</param>
    /// <returns>How many chunks were added.</returns>
    /// <exception cref="Storage.BrowserStorageQuotaException">
    /// The browser storage is full; the transaction was not committed (ADR 0099).
    /// </exception>
    ValueTask<int> WriteChunksAsync(
        string contentHash,
        int firstIndex,
        ArraySegment<byte> bytes,
        int chunkSize,
        long createdUtcMs,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads <paramref name="count"/> chunks from <paramref name="firstIndex"/> into
    /// <paramref name="destination"/> in one transaction, checking each one by its own hash, their
    /// numbers and their lengths (ADR 0272 §4).
    /// </summary>
    /// <param name="contentHash">SHA-256 of the whole file.</param>
    /// <param name="firstIndex">Number of the first chunk.</param>
    /// <param name="count">How many chunks to read.</param>
    /// <param name="destination">Exactly the bytes these chunks hold.</param>
    /// <param name="chunkSize">Bytes per chunk.</param>
    /// <param name="cancellationToken">Observed before the call starts.</param>
    /// <exception cref="InvalidDataException">A chunk is missing, out of order or damaged.</exception>
    ValueTask ReadChunksAsync(
        string contentHash,
        int firstIndex,
        int count,
        ArraySegment<byte> destination,
        int chunkSize,
        CancellationToken cancellationToken);
}

/// <summary>
/// The chunk store outside a browser, where IndexedDB does not exist: every call fails. A host off
/// the browser never stores file content in IndexedDB; tests give their own store.
/// </summary>
internal sealed class UnavailableFileContentChunkStore : IFileContentChunkStore
{
    /// <summary>Gets the one instance.</summary>
    internal static UnavailableFileContentChunkStore Instance { get; } = new();

    private UnavailableFileContentChunkStore()
    {
    }

    /// <inheritdoc />
    public ValueTask<string> HashAsync(ArraySegment<byte> bytes, CancellationToken cancellationToken) =>
        throw Unavailable();

    /// <inheritdoc />
    public ValueTask<int> WriteChunksAsync(
        string contentHash,
        int firstIndex,
        ArraySegment<byte> bytes,
        int chunkSize,
        long createdUtcMs,
        CancellationToken cancellationToken) => throw Unavailable();

    /// <inheritdoc />
    public ValueTask ReadChunksAsync(
        string contentHash,
        int firstIndex,
        int count,
        ArraySegment<byte> destination,
        int chunkSize,
        CancellationToken cancellationToken) => throw Unavailable();

    private static PlatformNotSupportedException Unavailable() =>
        new("File content is stored in the browser's IndexedDB, which this host does not have.");
}
