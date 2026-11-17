using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using System.Text;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Head;

/// <summary>
/// Implements the <c>head</c> terminal command (`P4-W5-CMD-003`, options extended by ADR 0064):
/// the first 10 lines of each file or of standard input; <c>-n N</c> (or <c>-N</c>) lines,
/// <c>-n -N</c> all but the last N, <c>-c N</c> bytes, <c>-q</c>/<c>-v</c> for the
/// <c>==&gt; file &lt;==</c> headers. Reading a file or standard input stops after what is printed,
/// so <c>yes | head</c> and <c>head -c 16 /dev/urandom</c> end (issue #542).
/// </summary>
public sealed class HeadCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public HeadCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();

        long count = 10;
        bool bytes = false, allButLast = false, quiet = false, verbose = false;
        List<string> files = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? value = null;
            if (a is "-n" or "-c" or "--lines" or "--bytes" && i + 1 < args.Count)
            {
                value = args[++i];
                bytes = a is "-c" or "--bytes";
            }
            else if ((a.StartsWith("-n", StringComparison.Ordinal) || a.StartsWith("-c", StringComparison.Ordinal)) && a.Length > 2)
            {
                value = a[2..];
                bytes = a[1] == 'c';
            }
            else if (a.Length > 1 && a[0] == '-' && a[1..].All(char.IsAsciiDigit))
            {
                value = a[1..];
            }
            else if (a is "-q" or "--quiet" or "--silent")
            {
                quiet = true;
                continue;
            }
            else if (a is "-v" or "--verbose")
            {
                verbose = true;
                continue;
            }
            else
            {
                files.Add(a);
                continue;
            }

            allButLast = value.StartsWith('-');
            if (!long.TryParse(value.TrimStart('-', '+'), out count))
            {
                context.StandardError.WriteLine(messages[bytes ? "InvalidByteCount" : "InvalidLineCount", value]);
                return 1;
            }
        }

        if (files.Count == 0)
        {
            files.Add("-");
        }

        int status = 0;
        bool headers = verbose || (files.Count > 1 && !quiet);
        for (int index = 0; index < files.Count; index++)
        {
            string file = files[index];
            string header = $"{(index > 0 ? "\n" : string.Empty)}==> {(file == "-" ? messages["StandardInput"] : file)} <==";
            if (!allButLast)
            {
                // Read only what is printed, then stop: the input may never end (yes | head,
                // head -c 16 /dev/urandom), and the writer upstream then gets "Broken pipe".
                if (file == "-")
                {
                    if (headers)
                    {
                        await context.StandardOutput.WriteLineAsync(header);
                    }

                    await (bytes
                        ? CopyFirstBytesAsync(context.StandardInput, context.StandardOutput, count, cancellationToken)
                        : CopyFirstLinesAsync(context.StandardInput, context.StandardOutput, count, cancellationToken));
                    continue;
                }

                Stream? content = await CommandIO.OpenContentAsync(context, file, "head", cancellationToken);
                if (content is null)
                {
                    status = 1;
                    continue;
                }

                await using (content)
                {
                    if (headers)
                    {
                        await context.StandardOutput.WriteLineAsync(header);
                    }

                    if (bytes)
                    {
                        await CopyFirstBytesAsync(content, context.StandardOutput, count, cancellationToken);
                    }
                    else
                    {
                        using StreamReader reader = new(content, Encoding.UTF8, leaveOpen: true);
                        await CopyFirstLinesAsync(reader, context.StandardOutput, count, cancellationToken);
                    }
                }

                continue;
            }

            // "All but the last N" needs the whole input, as in coreutils.
            string? text = await CommandIO.ReadTextAsync(context, file, "head", cancellationToken);
            if (text is null)
            {
                status = 1;
                continue;
            }

            if (headers)
            {
                await context.StandardOutput.WriteLineAsync(header);
            }

            if (bytes)
            {
                byte[] all = Encoding.UTF8.GetBytes(text);
                int length = (int)Math.Clamp(all.Length - count, 0, all.Length);
                await context.StandardOutput.WriteAsync(Encoding.UTF8.GetString(all, 0, length));
                continue;
            }

            List<string> lines = CommandIO.SplitLines(text);
            int take = (int)Math.Clamp(lines.Count - count, 0, lines.Count);
            foreach (string line in lines.Take(take))
            {
                await context.StandardOutput.WriteLineAsync(line);
            }
        }

        return status;
    }

    /// <summary>Size of one read from a file for <c>-c</c>: small enough for a terminal, large enough for a copy.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Writes the first <paramref name="count"/> lines of <paramref name="reader"/>, reading no further.</summary>
    private static async Task CopyFirstLinesAsync(TextReader reader, TextWriter output, long count, CancellationToken cancellationToken)
    {
        for (long written = 0; written < count && await reader.ReadLineAsync(cancellationToken) is { } line; written++)
        {
            await output.WriteLineAsync(line);
        }
    }

    /// <summary>
    /// Writes the first <paramref name="count"/> bytes of <paramref name="content"/>, reading at most
    /// that many. The terminal's output is text, so the bytes are decoded as UTF-8 (an invalid
    /// sequence, as in <c>/dev/urandom</c>, shows as U+FFFD).
    /// </summary>
    private static async Task CopyFirstBytesAsync(Stream content, TextWriter output, long count, CancellationToken cancellationToken)
    {
        Decoder decoder = Encoding.UTF8.GetDecoder();
        byte[] buffer = new byte[(int)Math.Clamp(count, 1, ChunkBytes)];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        long remaining = count;
        while (remaining > 0)
        {
            int read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            remaining -= read;
            int decoded = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
            await output.WriteAsync(chars.AsMemory(0, decoded), cancellationToken);
        }

        int tail = decoder.GetChars([], 0, 0, chars, 0, flush: true);
        await output.WriteAsync(chars.AsMemory(0, tail), cancellationToken);
    }

    /// <summary>
    /// Writes the characters of standard input that fit in its first <paramref name="count"/> bytes
    /// once encoded in UTF-8, reading no further. A character that would cross the limit is not
    /// written: standard input is text here, so half a character cannot be.
    /// </summary>
    private static async Task CopyFirstBytesAsync(TextReader reader, TextWriter output, long count, CancellationToken cancellationToken)
    {
        StringBuilder pending = new();
        char[] one = new char[2];
        long used = 0;
        while (used < count)
        {
            if (await reader.ReadAsync(one.AsMemory(0, 1), cancellationToken) == 0)
            {
                break;
            }

            int length = 1;
            if (char.IsHighSurrogate(one[0]) && await reader.ReadAsync(one.AsMemory(1, 1), cancellationToken) == 1)
            {
                length = 2;
            }

            int size = Encoding.UTF8.GetByteCount(one, 0, length);
            if (used + size > count)
            {
                break;
            }

            used += size;
            pending.Append(one, 0, length);
            if (pending.Length >= ChunkBytes)
            {
                await output.WriteAsync(pending.ToString());
                pending.Clear();
            }
        }

        await output.WriteAsync(pending.ToString());
    }
}
