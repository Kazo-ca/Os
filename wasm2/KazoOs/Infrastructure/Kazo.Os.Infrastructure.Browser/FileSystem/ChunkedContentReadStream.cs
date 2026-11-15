namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// Read-only, seekable stream over the chunks of one stored content that loads them only when they
/// are read (ADR 0275 §2). It keeps one window of consecutive chunks in memory, like the page cache
/// of a real disk keeps a read-ahead window: a read in the window costs nothing, a read past it
/// loads the next window. Reading straight on doubles the window, from
/// <see cref="InitialWindowChunks"/> up to <see cref="Schema.FileContentStoragePolicy.ChunksPerTransaction"/>
/// chunks (16 MiB); a seek elsewhere starts it small again, so jumping in a long recording loads
/// 1 MiB, not 16.
/// </summary>
/// <remarks>
/// <para>
/// The content is immutable (its key is its SHA-256), so the stream needs no lock against a
/// concurrent write: a file replaced while it is read keeps its old content, which the orphan
/// cleanup keeps for a day, or five minutes when space is low (ADR 0273). A chunk that is gone or
/// damaged makes the read fail with an <see cref="IOException"/>.
/// </para>
/// <para>
/// IndexedDB is only reachable asynchronously and the browser runs .NET on one thread, so a
/// synchronous read can only be served from the window already loaded; past it, it throws
/// <see cref="NotSupportedException"/>, like the browser's HTTP response streams do. The stream
/// opens with its first window loaded, so a small synchronous read of the start still works.
/// </para>
/// </remarks>
internal sealed class ChunkedContentReadStream : Stream
{
    /// <summary>Gets how many chunks the first window and the window after a seek hold: 1 MiB.</summary>
    internal const int InitialWindowChunks = 4;

    private readonly IndexedDbFileContentRepository _repository;
    private readonly string _contentHash;
    private readonly long _length;
    private readonly int _chunkSize;
    private byte[] _window = [];
    private long _windowStart;
    private int _windowLength;
    private int _windowChunks = InitialWindowChunks;
    private long _position;
    private bool _disposed;

    private ChunkedContentReadStream(IndexedDbFileContentRepository repository, string contentHash, long length)
    {
        _repository = repository;
        _contentHash = contentHash;
        _length = length;
        _chunkSize = repository.ChunkSize;
    }

    /// <summary>
    /// Opens the stream and loads its first window, so a content that is gone is reported when the
    /// file is opened, as for a file read whole.
    /// </summary>
    /// <param name="repository">Content repository that reads the chunks.</param>
    /// <param name="contentHash">SHA-256 of the whole file.</param>
    /// <param name="length">Length of the whole file, as its entry records it.</param>
    /// <param name="cancellationToken">Cancels the first load.</param>
    /// <exception cref="InvalidDataException">The first chunks are missing or damaged.</exception>
    internal static async ValueTask<ChunkedContentReadStream> OpenAsync(
        IndexedDbFileContentRepository repository,
        string contentHash,
        long length,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ChunkedContentReadStream stream = new(repository, contentHash, length);
        await stream.LoadWindowCoreAsync(0, cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <summary>Gets the first byte of the window in memory (for tests).</summary>
    internal long WindowStart => _windowStart;

    /// <summary>Gets how many bytes the window in memory holds (for tests).</summary>
    internal int WindowLength => _windowLength;

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        if (!InWindow(_position))
        {
            throw new NotSupportedException(
                "This file is read from browser storage as it is consumed, which can only be done "
                + "asynchronously: use ReadAsync or CopyToAsync.");
        }

        return CopyFromWindow(buffer);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        if (!InWindow(_position))
        {
            await LoadWindowAsync(_position, cancellationToken).ConfigureAwait(false);
        }

        return CopyFromWindow(buffer.Span);
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("File content streams are read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("File content streams are read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _window = [];
        _windowLength = 0;
        base.Dispose(disposing);
    }

    private bool InWindow(long position) => position >= _windowStart && position < _windowStart + _windowLength;

    private int CopyFromWindow(Span<byte> destination)
    {
        int offset = (int)(_position - _windowStart);
        int count = Math.Min(destination.Length, _windowLength - offset);
        _window.AsSpan(offset, count).CopyTo(destination);
        _position += count;
        return count;
    }

    /// <summary>Loads the window holding <paramref name="position"/>, reporting a lost content as an I/O error.</summary>
    private async ValueTask LoadWindowAsync(long position, CancellationToken cancellationToken)
    {
        try
        {
            await LoadWindowCoreAsync(position, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new IOException("The stored content of this file is damaged or no longer exists.", exception);
        }
    }

    /// <summary>
    /// Loads the window that starts at the chunk holding <paramref name="position"/>. A load right
    /// after the current window is a sequential read and doubles the window; anything else is a
    /// seek and starts it at <see cref="InitialWindowChunks"/>.
    /// </summary>
    private async ValueTask LoadWindowCoreAsync(long position, CancellationToken cancellationToken)
    {
        long start = position / _chunkSize * _chunkSize;
        bool sequential = _windowLength > 0 && start == _windowStart + _windowLength;
        int chunks = sequential
            ? Math.Min(_windowChunks * 2, Schema.FileContentStoragePolicy.ChunksPerTransaction)
            : InitialWindowChunks;
        int length = (int)Math.Min(_length - start, (long)chunks * _chunkSize);
        if (_window.Length < length)
        {
            // The first window gets a buffer of its own size, so a file only sought in costs 1 MiB.
            // Once a read goes on, the buffer goes straight to its largest size, one batch (16 MiB)
            // or the file: the stream never holds more, and never reallocates for each doubling.
            _window = new byte[_window.Length == 0
                ? length
                : (int)Math.Min(_length, (long)Schema.FileContentStoragePolicy.ChunksPerTransaction * _chunkSize)];
        }

        // Until the load succeeds the window is empty, so a failed load is never served from.
        _windowLength = 0;
        await _repository.ReadRangeAsync(
            _contentHash,
            _length,
            start,
            new ArraySegment<byte>(_window, 0, length),
            cancellationToken).ConfigureAwait(false);
        _windowStart = start;
        _windowLength = length;
        _windowChunks = chunks;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
