using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// The processes of a shell command on Windows: a job object the shell joins as soon as it starts, so that what it starts,
/// and what those start, join it too, also when a process between them exited (the <c>npm</c> shim that leaves
/// <c>node</c> without a parent). <see cref="Process.Kill(bool)"/> finds the tree by the parents that still run, and misses
/// those; terminating the job ends them all.
/// </summary>
/// <remarks>
/// Closing the job leaves its processes running: a command may start a program meant to outlive it. A process that
/// started before the shell joined the job is not in it; the shell starts in a fraction of a second, long before it runs
/// the command. macOS and Linux have none: the shell shares the process group of the application, and
/// <see cref="Process"/> cannot start it in a group of its own.
/// </remarks>
internal sealed class ShellProcessJob : IDisposable
{
    private nint _handle;

    private ShellProcessJob(nint handle)
    {
        _handle = handle;
    }

    /// <summary>Puts a shell that just started in a new job.</summary>
    /// <param name="process">The shell.</param>
    /// <returns>The job; null when the system has none, or the shell could not join it (it then runs as before).</returns>
    public static ShellProcessJob? TryJoin(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var handle = CreateJobObject(0, 0);
            if (handle == 0) return null;
            if (AssignProcessToJobObject(handle, process.Handle) == 0)
            {
                CloseHandle(handle);
                return null;
            }

            return new ShellProcessJob(handle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // The shell already exited, or the system does not have the calls.
            return null;
        }
    }

    /// <summary>Ends every process of the job, the shell included.</summary>
    public void Terminate()
    {
        var handle = Volatile.Read(ref _handle);
        if (handle != 0) _ = TerminateJobObject(handle, 1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) CloseHandle(handle);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, EntryPoint = "CreateJobObjectW")]
    private static extern nint CreateJobObject(nint attributes, nint name);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(nint handle);
}
