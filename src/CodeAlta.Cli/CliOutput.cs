using System.Text;

namespace CodeAlta.Cli;

/// <summary>
/// The writes of a run to its standard streams, serialized: standard output gets whole records (a line of JSONL, the
/// final answer), standard error whole lines, and both are flushed before the process exits.
/// </summary>
internal sealed class CliOutput
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stdout;
    private readonly TextWriter _stderr;
    private readonly object _stdoutGate = new();
    private readonly object _stderrGate = new();

    public CliOutput(CliStreams streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        _stdout = streams.Stdout;
        _stderr = streams.Stderr;
    }

    /// <summary>Writes UTF-8 bytes to standard output, followed by a line feed, at once.</summary>
    public void WriteLine(ReadOnlySpan<byte> utf8)
    {
        lock (_stdoutGate)
        {
            _stdout.Write(utf8);
            _stdout.WriteByte((byte)'\n');
            _stdout.Flush();
        }
    }

    /// <summary>Writes a text to standard output, ending it with a line feed when it has none.</summary>
    public void WriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Utf8.GetBytes(text.EndsWith('\n') ? text : text + "\n");
        lock (_stdoutGate)
        {
            _stdout.Write(bytes);
            _stdout.Flush();
        }
    }

    /// <summary>Writes a line to standard error.</summary>
    public void Error(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_stderrGate)
        {
            _stderr.WriteLine(line);
            _stderr.Flush();
        }
    }

    /// <summary>Flushes both streams; a stream the reader closed is not an error at exit.</summary>
    public void Flush()
    {
        try
        {
            lock (_stdoutGate) _stdout.Flush();
        }
        catch (IOException)
        {
        }

        try
        {
            lock (_stderrGate) _stderr.Flush();
        }
        catch (IOException)
        {
        }
    }
}
