using System.Threading.Channels;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Reads the lines a child process writes to one of its redirected outputs.
/// </summary>
/// <remarks>
/// On Windows the redirected pipes of a process are synchronous: an asynchronous read of one holds a thread of the pool,
/// blocked until the process writes, for as long as the process runs. There a thread of its own reads the lines, at most
/// <see cref="ReadAheadLines"/> ahead of the consumer (the pipe makes the process wait beyond that), and hands them over in
/// order; the end of the output, and an error of a read after the lines read before it, come as they would from the
/// stream. A wait for a line can be cancelled and loses no line. Elsewhere the reads are asynchronous and go to the stream.
/// </remarks>
internal sealed class ProcessOutputLineReader : IDisposable
{
    internal const int ReadAheadLines = 16;

    private readonly StreamReader _reader;
    private readonly Channel<string>? _lines;

    private ProcessOutputLineReader(StreamReader reader, Channel<string>? lines)
    {
        _reader = reader;
        _lines = lines;
    }

    /// <summary>Gets whether a thread of its own reads the lines.</summary>
    public bool UsesDedicatedThread => _lines is not null;

    /// <summary>Starts reading the lines of an output, on a thread of its own on Windows.</summary>
    public static ProcessOutputLineReader Start(StreamReader reader, string threadName)
        => Start(reader, threadName, OperatingSystem.IsWindows());

    /// <summary>Starts reading the lines of an output, on a thread of its own when asked.</summary>
    public static ProcessOutputLineReader Start(StreamReader reader, string threadName, bool dedicatedThread)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        if (!dedicatedThread)
        {
            return new ProcessOutputLineReader(reader, null);
        }

        var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(ReadAheadLines)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var thread = new Thread(static state =>
        {
            var (source, target) = ((StreamReader, ChannelWriter<string>))state!;
            Pump(source, target);
        })
        {
            IsBackground = true,
            Name = threadName,
        };
        thread.Start((reader, lines.Writer));
        return new ProcessOutputLineReader(reader, lines);
    }

    /// <summary>Reads the next line, or <see langword="null" /> at the end of the output.</summary>
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        => _lines is null ? _reader.ReadLineAsync(cancellationToken) : ReadHandedOverLineAsync(_lines.Reader, cancellationToken);

    /// <summary>
    /// Hands no more lines over: a read gets the lines already read, then the end. The thread ends at the next line it
    /// reads, or at the end of the output.
    /// </summary>
    public void Dispose() => _lines?.Writer.TryComplete();

    private static async ValueTask<string?> ReadHandedOverLineAsync(ChannelReader<string> lines, CancellationToken cancellationToken)
    {
        // Once the lines read before it are taken, the wait throws what the read of the thread threw.
        while (await lines.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (lines.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }

    private static void Pump(StreamReader reader, ChannelWriter<string> lines)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                while (!lines.TryWrite(line))
                {
                    // The consumer is that many lines behind: wait for it. Once disposed it takes no more.
                    if (!lines.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
                    {
                        return;
                    }
                }
            }

            lines.TryComplete();
        }
        catch (Exception ex)
        {
            lines.TryComplete(ex);
        }
    }
}
