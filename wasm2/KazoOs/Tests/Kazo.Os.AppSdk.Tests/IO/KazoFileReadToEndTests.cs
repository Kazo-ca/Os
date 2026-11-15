using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>
/// Verifies how <c>KazoFile.ReadAllBytesAsync</c> drains a read stream (ADR 0275 §2): straight into
/// an array of the announced size, and still correct when the stream lies about its length.
/// </summary>
public sealed class KazoFileReadToEndTests
{
    [Fact]
    public async Task Seekable_stream_is_read_from_its_position_to_the_end()
    {
        MemoryStream source = new([1, 2, 3, 4, 5]) { Position = 1 };

        byte[] bytes = await KazoFileSystemCore.ReadToEndAsync(source, CancellationToken.None);

        Assert.Equal([2, 3, 4, 5], bytes);
    }

    [Fact]
    public async Task Stream_shorter_than_announced_returns_what_it_has()
    {
        byte[] bytes = await KazoFileSystemCore.ReadToEndAsync(new LyingStream([1, 2, 3], announced: 10), CancellationToken.None);

        Assert.Equal([1, 2, 3], bytes);
    }

    [Fact]
    public async Task Stream_longer_than_announced_is_read_to_its_real_end()
    {
        byte[] bytes = await KazoFileSystemCore.ReadToEndAsync(new LyingStream([1, 2, 3, 4, 5], announced: 2), CancellationToken.None);

        Assert.Equal([1, 2, 3, 4, 5], bytes);
    }

    [Fact]
    public async Task Stream_of_unknown_length_is_buffered()
    {
        byte[] bytes = await KazoFileSystemCore.ReadToEndAsync(new LyingStream([7, 8], announced: null), CancellationToken.None);

        Assert.Equal([7, 8], bytes);
    }

    /// <summary>A seekable-looking stream whose <see cref="Length"/> is wrong or unknown.</summary>
    private sealed class LyingStream(byte[] content, long? announced) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => announced is not null;

        public override bool CanWrite => false;

        public override long Length => announced ?? throw new NotSupportedException();

        public override long Position
        {
            get => announced is null ? throw new NotSupportedException() : 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 2));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
