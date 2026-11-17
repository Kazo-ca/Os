using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Head;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary>
/// <c>head</c> on files (issue #542): it must stop after what it prints, so it ends on an endless
/// device such as <c>/dev/zero</c>, and <c>-c</c> counts bytes rather than characters.
/// </summary>
public sealed class HeadCommandTests
{
    [Fact]
    public async Task Head_c_on_an_endless_device_ends_and_reads_only_what_it_needs()
    {
        EndlessStream device = new((byte)'z');
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithStream("/dev/zero", () => device);

        (int exit, string output) = await RunAsync(fs, "-c", "16", "/dev/zero").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exit);
        Assert.Equal(new string('z', 16), output);
        Assert.True(device.BytesRead < 64 * 1024, $"head read {device.BytesRead} bytes for 16");
    }

    [Fact]
    public async Task Head_n_on_an_endless_device_ends_after_its_lines()
    {
        EndlessStream device = new((byte)'\n');
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithStream("/dev/newlines", () => device);

        (int exit, string output) = await RunAsync(fs, "-n", "3", "/dev/newlines").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exit);
        Assert.Equal("\n\n\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(device.BytesRead < 64 * 1024, $"head read {device.BytesRead} bytes for 3 lines");
    }

    [Fact]
    public async Task Head_c_counts_bytes_not_characters()
    {
        // "é" is two bytes in UTF-8: the first 3 bytes of "aébc" are "aé".
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/a.txt", "aébc");

        (int exit, string output) = await RunAsync(fs, "-c", "3", "a.txt");

        Assert.Equal(0, exit);
        Assert.Equal("aé", output);
    }

    [Fact]
    public async Task Head_c_minus_N_still_prints_all_but_the_last_bytes()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/a.txt", "abcdef");

        (int exit, string output) = await RunAsync(fs, "-c", "-2", "a.txt");

        Assert.Equal(0, exit);
        Assert.Equal("abcd", output);
    }

    [Fact]
    public async Task Head_n_on_a_file_prints_its_first_lines_with_headers_for_several_files()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/user/a.txt", "1\n2\n3\n")
            .WithFile("/home/user/b.txt", "x\ny\n");

        (int exit, string output) = await RunAsync(fs, "-n", "2", "a.txt", "b.txt");

        Assert.Equal(0, exit);
        Assert.Equal("==> a.txt <==\n1\n2\n\n==> b.txt <==\nx\ny\n", output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Head_c_on_standard_input_stops_after_its_bytes()
    {
        using EndlessReader stdin = new('y');
        TerminalExecutionContext context = CreateContext(new FakeAppFileSystemGateway(), stdin, "-c", "5");

        int exit = await new HeadCommand(AppDefinitionMapper.ToManifest(new HeadApp()))
            .ExecuteAsync(context, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exit);
        Assert.Equal("yyyyy", context.StandardOutput.ToString());
    }

    [Fact]
    public async Task A_missing_file_is_reported_and_fails()
    {
        TerminalExecutionContext context = CreateContext(new FakeAppFileSystemGateway(), new StringReader(string.Empty), "-c", "4", "nope.txt");

        int exit = await new HeadCommand(AppDefinitionMapper.ToManifest(new HeadApp())).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("nope.txt", context.StandardError.ToString(), StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Output)> RunAsync(FakeAppFileSystemGateway fs, params string[] arguments)
    {
        TerminalExecutionContext context = CreateContext(fs, new StringReader(string.Empty), arguments);
        int exit = await new HeadCommand(AppDefinitionMapper.ToManifest(new HeadApp())).ExecuteAsync(context, CancellationToken.None);
        return (exit, context.StandardOutput.ToString()!);
    }

    private static TerminalExecutionContext CreateContext(
        FakeAppFileSystemGateway fs, TextReader stdin, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new HeadApp()), fileSystem: fs),
        arguments,
        stdin,
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string>());

    /// <summary>A read-only stream that never ends, counting what was read from it.</summary>
    private sealed class EndlessStream(byte fill) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, 4096);
            buffer.AsSpan(offset, n).Fill(fill);
            BytesRead += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Standard input that never ends (like <c>yes</c> without newlines).</summary>
    private sealed class EndlessReader(char fill) : TextReader
    {
        public override int Peek() => fill;

        public override int Read() => fill;

        public override int Read(char[] buffer, int index, int count)
        {
            buffer.AsSpan(index, count).Fill(fill);
            return count;
        }
    }
}
