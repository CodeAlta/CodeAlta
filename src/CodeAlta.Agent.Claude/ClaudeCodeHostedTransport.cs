using System.Text;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// The lines exchanged with a CLI process that a host started elsewhere than as a child of this process: in the
/// sandbox of the session. It behaves as <see cref="ClaudeCodeProcessTransport"/>.
/// </summary>
internal sealed class ClaudeCodeHostedTransport : IClaudeCodeTransport
{
    private const int StandardErrorTailLimit = 8 * 1024;
    private static readonly TimeSpan GracefulExit = TimeSpan.FromSeconds(3);
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly IAgentHostedProcess _process;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _standardError = new();
    private readonly ProcessOutputLineReader _output;
    private readonly ProcessOutputLineReader _error;
    private readonly Task _standardErrorPump;
    private int _disposed;

    private ClaudeCodeHostedTransport(IAgentHostedProcess process)
    {
        _process = process;
        _output = ProcessOutputLineReader.Start(new StreamReader(process.StandardOutput, Encoding.UTF8), "Claude Code output");
        _error = ProcessOutputLineReader.Start(new StreamReader(process.StandardError, Encoding.UTF8), "Claude Code error output");
        _standardErrorPump = PumpStandardErrorAsync();
    }

    /// <summary>Starts the CLI through a host.</summary>
    /// <exception cref="InvalidOperationException">The host could not start it.</exception>
    public static async Task<IClaudeCodeTransport> StartAsync(IAgentProcessHost host, AgentProcessLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launch);
        return new ClaudeCodeHostedTransport(await host.StartAsync(launch, cancellationToken).ConfigureAwait(false));
    }

    public string StandardErrorTail
    {
        get
        {
            lock (_standardError) return _standardError.ToString().Trim();
        }
    }

    public int? ExitCode => _process.Exited.IsCompletedSuccessfully ? _process.Exited.Result : null;

    public int? ProcessId => _process.Id;

    public async ValueTask WriteLineAsync(ReadOnlyMemory<byte> utf8Line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A line is written whole or the process is given up: a cancelled half line would corrupt the next one.
            await _process.StandardInput.WriteAsync(utf8Line, CancellationToken.None).ConfigureAwait(false);
            await _process.StandardInput.WriteAsync(NewLine, CancellationToken.None).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
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
            return await _output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
            await _process.Exited.WaitAsync(GracefulExit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _process.Kill();
        }

        try
        {
            await _standardErrorPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
        }

        _output.Dispose();
        _error.Dispose();
        await _process.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            while (await _error.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                lock (_standardError)
                {
                    _standardError.AppendLine(line);
                    if (_standardError.Length > StandardErrorTailLimit) _standardError.Remove(0, _standardError.Length - StandardErrorTailLimit);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
