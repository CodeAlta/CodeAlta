using System.Diagnostics;
using System.Text;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// How one CLI process is started.
/// </summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">Its arguments, one per element.</param>
/// <param name="WorkingDirectory">The folder it works in.</param>
/// <param name="Environment">Variables set for the process; a <see langword="null" /> value removes an inherited one.</param>
internal sealed record ClaudeCodeLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

/// <summary>
/// The lines exchanged with one CLI process: JSON documents, one per line, in both directions.
/// </summary>
internal interface IClaudeCodeTransport : IAsyncDisposable
{
    /// <summary>Writes one line to the input of the process.</summary>
    ValueTask WriteLineAsync(ReadOnlyMemory<byte> utf8Line, CancellationToken cancellationToken);

    /// <summary>Reads the next line of the output of the process, or <see langword="null" /> when it ended.</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Gets the end of what the process wrote to its error output.</summary>
    string StandardErrorTail { get; }

    /// <summary>Gets the exit code of the process once it exited.</summary>
    int? ExitCode { get; }
}

/// <summary>
/// Starts CLI processes.
/// </summary>
internal interface IClaudeCodeTransportFactory
{
    /// <summary>Starts a process.</summary>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    IClaudeCodeTransport Start(ClaudeCodeLaunch launch);
}

/// <summary>
/// Runs the CLI as a child process with redirected input and output.
/// </summary>
internal sealed class ClaudeCodeProcessTransportFactory : IClaudeCodeTransportFactory
{
    public static ClaudeCodeProcessTransportFactory Instance { get; } = new();

    public IClaudeCodeTransport Start(ClaudeCodeLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(launch.WorkingDirectory))
        {
            startInfo.WorkingDirectory = launch.WorkingDirectory;
        }

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Claude Code could not be started from '{launch.FileName}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Claude Code could not be started from '{launch.FileName}': {ex.Message}", ex);
        }

        return new ClaudeCodeProcessTransport(process);
    }
}

internal sealed class ClaudeCodeProcessTransport : IClaudeCodeTransport
{
    private const int StandardErrorTailLimit = 8 * 1024;
    private static readonly TimeSpan GracefulExit = TimeSpan.FromSeconds(3);
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly Process _process;
    private readonly Stream _input;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _standardError = new();
    private readonly Task _standardErrorPump;
    private int _disposed;

    public ClaudeCodeProcessTransport(Process process)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _standardErrorPump = PumpStandardErrorAsync();
    }

    public string StandardErrorTail
    {
        get
        {
            lock (_standardError)
            {
                return _standardError.ToString().Trim();
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public async ValueTask WriteLineAsync(ReadOnlyMemory<byte> utf8Line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A line is written whole or the process is given up: a cancelled half line would corrupt the next one.
            await _input.WriteAsync(utf8Line, CancellationToken.None).ConfigureAwait(false);
            await _input.WriteAsync(NewLine, CancellationToken.None).ConfigureAwait(false);
            await _input.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new IOException("Claude Code no longer reads its input.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The CLI saves its transcript and exits when its input ends: it is given the time to do so.
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        try
        {
            using var graceful = new CancellationTokenSource(GracefulExit);
            await _process.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                using var forced = new CancellationTokenSource(GracefulExit);
                await _process.WaitForExitAsync(forced.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or NotSupportedException)
            {
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await _standardErrorPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
        }

        _process.Dispose();
        _writeGate.Dispose();
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_standardError)
                {
                    _standardError.AppendLine(line);
                    if (_standardError.Length > StandardErrorTailLimit)
                    {
                        _standardError.Remove(0, _standardError.Length - StandardErrorTailLimit);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
