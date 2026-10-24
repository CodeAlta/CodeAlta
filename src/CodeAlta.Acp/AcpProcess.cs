using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using XenoAtom.Logging;

namespace CodeAlta.Acp;

/// <summary>
/// The process of an ACP agent: its standard output carries the protocol, its standard error is read to the log and
/// its last lines are kept to explain an exit.
/// </summary>
/// <remarks>
/// On Windows the redirected pipes of a process are synchronous: an asynchronous read of one holds a thread of the pool
/// for as long as the process runs. There a thread of its own reads each output, the protocol lines at most
/// <see cref="ReadAheadLines"/> ahead of the transport (the pipe makes the agent wait beyond that).
/// </remarks>
internal sealed class AcpProcess : IAsyncDisposable
{
    internal const int ReadAheadLines = 64;
    internal const int ErrorTailLines = 40;

    private readonly Process _process;
    private readonly Logger? _logger;
    private readonly Queue<string> _errorTail = new();
    private readonly Lock _errorTailLock = new();
    private readonly Channel<string>? _outputLines;
    private readonly ManualResetEventSlim _errorsEnded = new(false);
    private readonly StreamReader _output;
    private int _disposed;

    private AcpProcess(Process process, Logger? logger, bool dedicatedThreads)
    {
        _process = process;
        _logger = logger;
        _output = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        if (dedicatedThreads)
        {
            _outputLines = Channel.CreateBounded<string>(new BoundedChannelOptions(ReadAheadLines)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
            StartThread("ACP agent output", () => PumpOutput(_output, _outputLines.Writer));
        }

        StartThread("ACP agent errors", PumpErrors);
    }

    /// <summary>Gets the standard input of the agent.</summary>
    public Stream StandardInput => _process.StandardInput.BaseStream;

    /// <summary>Gets the process id of the agent.</summary>
    public int Id => _process.Id;

    /// <summary>Gets whether the agent exited.</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Gets the exit code of the agent, or <see langword="null" /> while it runs.</summary>
    public int? ExitCode => HasExited ? TryGetExitCode() : null;

    /// <summary>
    /// Waits a little for the process to exit and its standard error to be read to its end, then returns its exit code
    /// and the last lines of its standard error: the protocol output can end before the last error lines are read.
    /// </summary>
    /// <param name="wait">How long to wait at most.</param>
    public (int? ExitCode, string ErrorTail) DescribeExit(TimeSpan wait)
    {
        try
        {
            _process.WaitForExit(wait);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Disposed or never started.
        }

        _errorsEnded.Wait(wait);
        return (ExitCode, ErrorTail);
    }

    /// <summary>Gets the last lines the agent wrote to its standard error.</summary>
    public string ErrorTail
    {
        get
        {
            lock (_errorTailLock)
            {
                return string.Join('\n', _errorTail);
            }
        }
    }

    /// <summary>Starts the process of an agent.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">Thrown when the file name is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the process cannot be started.</exception>
    public static AcpProcess Start(AcpProcessOptions options, Logger? logger = null)
        => Start(options, logger, OperatingSystem.IsWindows());

    /// <summary>Starts the process of an agent, reading its outputs on threads of their own when asked.</summary>
    internal static AcpProcess Start(AcpProcessOptions options, Logger? logger, bool dedicatedThreads)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        if (options.Arguments is { Count: > 0 })
        {
            foreach (var argument in options.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (options.RemovedEnvironmentVariables is { Count: > 0 })
        {
            foreach (var name in options.RemovedEnvironmentVariables)
            {
                startInfo.Environment.Remove(name);
            }
        }

        if (options.EnvironmentVariables is not null)
        {
            foreach (var pair in options.EnvironmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start ACP process '{options.FileName}'.");
        return new AcpProcess(process, logger, dedicatedThreads);
    }

    /// <summary>Reads the next protocol line, or <see langword="null" /> at the end of the output.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_outputLines is null)
        {
            return await _output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        var lines = _outputLines.Reader;
        while (await lines.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (lines.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outputLines?.Writer.TryComplete();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process ended by itself in the meantime.
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            _logger?.Warn($"The ACP agent process did not exit in time after it was killed.");
        }

        _process.Dispose();
    }

    private static void StartThread(string name, Action body)
    {
        var thread = new Thread(() => body())
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
    }

    private static void PumpOutput(StreamReader reader, ChannelWriter<string> lines)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                while (!lines.TryWrite(line))
                {
                    // The transport is that many lines behind: wait for it. Once disposed it takes no more.
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

    private void PumpErrors()
    {
        try
        {
            while (_process.StandardError.ReadLine() is { } line)
            {
                _logger?.Debug($"ACP agent stderr: {line}");
                lock (_errorTailLock)
                {
                    _errorTail.Enqueue(line.Length > 1000 ? line[..1000] : line);
                    while (_errorTail.Count > ErrorTailLines)
                    {
                        _errorTail.Dequeue();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process ended.
        }
        finally
        {
            _errorsEnded.Set();
        }
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
