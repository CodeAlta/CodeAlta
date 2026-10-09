using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Orchestration.Jobs;

/// <summary>A command the host runs by itself, outside a tool call: it is read while it runs and can be ended.</summary>
public interface IShellCommandProcess : IDisposable
{
    /// <summary>Gets the identifier of the process of the shell; null when the host does not know it.</summary>
    int? ProcessId { get; }

    /// <summary>
    /// Gets the task that ends with the exit code of the command, once everything it wrote was given to the
    /// reader. It never faults: a command that could not be waited for ends with <c>-1</c>.
    /// </summary>
    Task<int> Completion { get; }

    /// <summary>Ends the command and the processes it started. A command that already ended is left alone.</summary>
    void Kill();
}

/// <summary>Starts a command for a host that runs it by itself.</summary>
/// <param name="command">The command line, as a shell tool would take it.</param>
/// <param name="folder">The folder the command runs in: a full path that exists.</param>
/// <param name="onOutput">
/// Receives what the command writes, standard output and standard error together, in pieces as they are read. It
/// is called from the threads that read, possibly from two of them at once.
/// </param>
/// <returns>The running command.</returns>
/// <exception cref="InvalidOperationException">The shell could not be started.</exception>
public delegate IShellCommandProcess ShellCommandStarter(string command, string folder, Action<string> onOutput);

/// <summary>
/// A command started in the shell the <c>shell_command</c> tool uses (PowerShell on Windows, the shell of the user
/// elsewhere), with nothing on its standard input and with what it writes read as it comes.
/// </summary>
public sealed class ShellCommandProcess : IShellCommandProcess
{
    // How long the outputs are still read once the shell has exited: a process it left behind may hold them open.
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly CancellationTokenSource _reading = new();
    private int _disposed;

    private ShellCommandProcess(Process process, Action<string> onOutput)
    {
        _process = process;
        try { ProcessId = process.Id; }
        catch (InvalidOperationException) { ProcessId = null; }
        var output = PumpAsync(process.StandardOutput, onOutput, _reading.Token);
        var error = PumpAsync(process.StandardError, onOutput, _reading.Token);
        Completion = WaitAsync(output, error);
    }

    /// <inheritdoc />
    public int? ProcessId { get; }

    /// <inheritdoc />
    public Task<int> Completion { get; }

    /// <summary>Starts a command.</summary>
    /// <param name="command">The command line, as the <c>shell_command</c> tool would take it.</param>
    /// <param name="folder">The folder the command runs in: a full path that exists.</param>
    /// <param name="onOutput">Receives what the command writes; see <see cref="ShellCommandStarter"/>.</param>
    /// <returns>The running command.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> or <paramref name="folder"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="onOutput"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The shell could not be started.</exception>
    public static ShellCommandProcess Start(string command, string folder, Action<string> onOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(onOutput);
        var startInfo = AgentBuiltInToolFactory.CreateShellStartInfo(command, folder);
        // Nobody types in a command that runs by itself: one that reads its input gets the end of it at once.
        startInfo.RedirectStandardInput = true;
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The shell did not start a process.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            process.Dispose();
            throw new InvalidOperationException($"The shell could not be started: {exception.Message}", exception);
        }

        try { process.StandardInput.Close(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
        return new ShellCommandProcess(process, onOutput);
    }

    /// <inheritdoc />
    public void Kill()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // The process ended in between, or a part of its tree could not be ended: there is nothing more to do.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
        _reading.Cancel();
        _process.Dispose();
        _reading.Dispose();
    }

    private async Task<int> WaitAsync(Task output, Task error)
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            var read = Task.WhenAll(output, error);
            if (await Task.WhenAny(read, Task.Delay(OutputGrace)).ConfigureAwait(false) != read)
            {
                try { _reading.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            await read.ConfigureAwait(false);
            return _process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ObjectDisposedException or OperationCanceledException)
        {
            return -1;
        }
    }

    // Reads in pieces, not in lines: a progress line that is rewritten in place is shown as it is written.
    private static async Task PumpAsync(StreamReader reader, Action<string> onOutput, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0) return;
                onOutput(new string(buffer, 0, read));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The command was ended, or its output is held by a process it left behind: what was read is kept.
        }
    }
}
