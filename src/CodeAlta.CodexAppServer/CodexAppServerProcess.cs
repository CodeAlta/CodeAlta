using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using XenoAtom.Logging;

namespace CodeAlta.CodexAppServer;

/// <summary>How a Codex app-server process is started as a child of this process.</summary>
public sealed class CodexAppServerStartInfo
{
    /// <summary>Gets the executable: <c>codex-app-server</c>, or the <c>codex</c> CLI with <c>app-server</c> as its first argument.</summary>
    public required string FileName { get; init; }

    /// <summary>Gets its arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets its working directory, or <see langword="null"/>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the variables set for it; a <see langword="null"/> value removes one.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// The process of a Codex app-server: its standard output and input carry the protocol, one JSON message a line; its
/// standard error is read to the log, and its last lines explain an exit. It is either a child of this process
/// (<see cref="Start"/>), or a process another component started, such as the sandbox of a session.
/// </summary>
/// <remarks>
/// On Windows the redirected pipes of a child are synchronous: an asynchronous read holds a thread of the pool for as
/// long as the process runs. There a thread of its own reads each output.
/// </remarks>
public sealed class CodexAppServerProcess : IAsyncDisposable
{
    private const int ErrorTailLines = 40;
    private const int ReadAheadLines = 64;

    private readonly Queue<string> _errorTail = new();
    private readonly Lock _errorTailLock = new();
    private readonly TaskCompletionSource _errorsEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<CancellationToken, ValueTask<string?>> _readLine;
    private readonly Func<ValueTask> _end;
    private readonly Logger? _logger;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="CodexAppServerProcess"/> class for a process another component started, and starts reading its errors.</summary>
    /// <param name="name">The name of the process, as its exit is told.</param>
    /// <param name="standardOutput">What the process writes: the protocol.</param>
    /// <param name="standardInput">What the process reads: the protocol.</param>
    /// <param name="standardError">What the process writes as errors.</param>
    /// <param name="exited">A task that ends with the exit code of the process.</param>
    /// <param name="end">Ends the process and what it started, and releases what started it.</param>
    /// <param name="logger">The log of its errors, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CodexAppServerProcess(string name, Stream standardOutput, Stream standardInput, Stream standardError, Task<int> exited, Func<ValueTask> end, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        Name = name ?? throw new ArgumentNullException(nameof(name));
        StandardInput = standardInput ?? throw new ArgumentNullException(nameof(standardInput));
        Exited = exited ?? throw new ArgumentNullException(nameof(exited));
        _end = end ?? throw new ArgumentNullException(nameof(end));
        _logger = logger;
        var reader = new StreamReader(standardOutput, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        _readLine = cancellationToken => reader.ReadLineAsync(cancellationToken);
        _ = Task.Run(() => ReadErrorsAsync(new StreamReader(standardError, new UTF8Encoding(false))));
    }

    private CodexAppServerProcess(string name, Process process, Logger? logger)
    {
        Name = name;
        _logger = logger;
        StandardInput = process.StandardInput.BaseStream;
        var output = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        if (OperatingSystem.IsWindows())
        {
            var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(ReadAheadLines) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            StartThread("Codex app-server output", () => PumpOutput(output, lines.Writer));
            _readLine = async cancellationToken =>
            {
                while (await lines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (lines.Reader.TryRead(out var line))
                    {
                        return line;
                    }
                }

                return null;
            };
        }
        else
        {
            _readLine = cancellationToken => output.ReadLineAsync(cancellationToken);
        }

        StartThread("Codex app-server errors", () => ReadErrorsAsync(process.StandardError).GetAwaiter().GetResult());
        Exited = WaitForExitAsync(process);
        _end = () => new ValueTask(EndChildAsync(process));
    }

    /// <summary>Gets the name of the process.</summary>
    public string Name { get; }

    /// <summary>Gets what the process reads: the protocol.</summary>
    public Stream StandardInput { get; }

    /// <summary>Gets a task that ends with the exit code of the process.</summary>
    public Task<int> Exited { get; }

    /// <summary>Gets the last lines the process wrote to its standard error.</summary>
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

    /// <summary>Starts a Codex app-server as a child of this process.</summary>
    /// <param name="startInfo">What it is started with.</param>
    /// <param name="logger">The log of its errors, or <see langword="null"/>.</param>
    /// <returns>The process.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="startInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    public static CodexAppServerProcess Start(CodexAppServerStartInfo startInfo, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.FileName);
        var start = new ProcessStartInfo
        {
            FileName = startInfo.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
        {
            start.WorkingDirectory = startInfo.WorkingDirectory;
        }

        foreach (var argument in startInfo.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in startInfo.Environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"'{startInfo.FileName}' could not be started.");
        return new CodexAppServerProcess(Path.GetFileNameWithoutExtension(startInfo.FileName), process, logger);
    }

    /// <summary>Reads the next protocol line, or <see langword="null"/> at the end of the output.</summary>
    /// <param name="cancellationToken">A token to cancel the reading.</param>
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => _readLine(cancellationToken);

    /// <summary>Waits a little for the process to exit and its errors to be read, then says how it ended.</summary>
    /// <returns>The exit code, when known, and the last lines of its errors.</returns>
    public string DescribeExit()
    {
        try
        {
            Task.WhenAll(Exited, _errorsEnded.Task).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Said by what is known below.
        }

        var message = Exited.IsCompletedSuccessfully
            ? $"The Codex app-server ({Name}) exited with code {Exited.Result}."
            : $"The Codex app-server ({Name}) closed its output.";
        var tail = ErrorTail.Trim();
        return tail.Length == 0 ? message : $"{message}{Environment.NewLine}{tail}";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            StandardInput.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        await _end().ConfigureAwait(false);
    }

    private async Task EndChildAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process ended by itself in the meantime.
        }

        try
        {
            // The task of the exit reads its code from the process: it ends before the process is disposed.
            await Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger?.Warn("The Codex app-server did not exit in time after it was killed.");
        }

        process.Dispose();
    }

    // The exit code of the process; -1 when it was disposed before its code could be read (it did not exit in time
    // after it was killed): nothing waits on this task to see it fail.
    private static async Task<int> WaitForExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void StartThread(string name, Action body)
        => new Thread(() => body()) { IsBackground = true, Name = name }.Start();

    private static void PumpOutput(StreamReader reader, ChannelWriter<string> lines)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                while (!lines.TryWrite(line))
                {
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

    private async Task ReadErrorsAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _logger?.Debug($"Codex app-server stderr: {line}");
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
            _errorsEnded.TrySetResult();
        }
    }
}
