using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace CodeAlta.NativeEngines;

/// <summary>A worker as a process the host talks to through its standard streams.</summary>
public interface IWorkerProcess : IDisposable
{
    /// <summary>Gets the stream the host writes frames to.</summary>
    Stream Input { get; }

    /// <summary>Gets the stream the host reads frames from.</summary>
    Stream Output { get; }

    /// <summary>Ends the process at once.</summary>
    void Kill();
}

/// <summary>A worker process: this executable started again with the hidden option of the worker.</summary>
public sealed class WorkerProcess : IWorkerProcess
{
    private readonly Process _process;

    private WorkerProcess(Process process, Action<string>? errorLine)
    {
        _process = process;
        // A native engine writes what it refuses on its error stream, which may be all it says before it ends.
        process.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data)) errorLine?.Invoke(line.Data);
        };
        process.BeginErrorReadLine();
    }

    /// <inheritdoc />
    public Stream Input => _process.StandardInput.BaseStream;

    /// <inheritdoc />
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>Gets the id of the process.</summary>
    public int Id => _process.Id;

    /// <summary>Starts this executable as a worker in a folder.</summary>
    /// <param name="option">The hidden option the worker is started with, such as <c>--speech-worker</c>.</param>
    /// <param name="workingDirectory">Its folder.</param>
    /// <param name="errorLine">Gets each line the worker writes on its error stream, or null.</param>
    /// <returns>The worker.</returns>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    /// <exception cref="InvalidOperationException">This process has no executable to start again.</exception>
    public static WorkerProcess Start(string option, string workingDirectory, Action<string>? errorLine)
        => Start(Environment.ProcessPath ?? throw new InvalidOperationException("The path of the application is unknown."), option, workingDirectory, errorLine);

    /// <summary>Starts an executable as a worker in a folder.</summary>
    /// <param name="executable">The executable.</param>
    /// <param name="option">The hidden option the worker is started with.</param>
    /// <param name="workingDirectory">Its folder.</param>
    /// <param name="errorLine">Gets each line the worker writes on its error stream, or null.</param>
    /// <returns>The worker.</returns>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    public static WorkerProcess Start(string executable, string option, string workingDirectory, Action<string>? errorLine)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentException.ThrowIfNullOrEmpty(option);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // Started by the dotnet host, the application is the first argument of that host.
#pragma warning disable IL3000 // Read only when the process runs under the dotnet host, which is never a single-file app.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
            start.ArgumentList.Add(entry);
#pragma warning restore IL3000
        start.ArgumentList.Add(option);
        return new(Process.Start(start) ?? throw new InvalidOperationException("The worker did not start."), errorLine);
    }

    /// <summary>Lowers the priority of the worker, for work in the background. Best effort.</summary>
    public void LowerPriority()
    {
        try
        {
            if (!_process.HasExited) _process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    /// <inheritdoc />
    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();
}
