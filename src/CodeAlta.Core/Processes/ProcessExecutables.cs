using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeAlta.Core.Processes;

/// <summary>
/// The executables of the processes that run now, as far as this process may read them: on Linux the link
/// <c>/proc/&lt;pid&gt;/exe</c>, on macOS <c>proc_pidpath</c>, on Windows the main module. A process that cannot be read (of
/// another user, protected, or gone meanwhile) is skipped.
/// </summary>
public static partial class ProcessExecutables
{
    // PROC_PIDPATHINFO_MAXSIZE: 4 * MAXPATHLEN.
    private const int MaximumPathBytes = 4 * 1024;

    /// <summary>Lists the full paths of the executables of the running processes, each once.</summary>
    /// <returns>The paths, in no order.</returns>
    public static IReadOnlyCollection<string> List()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            return paths;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                if (PathOf(process) is { Length: > 0 } path) paths.Add(path);
            }
        }

        return paths;
    }

    private static string? PathOf(Process process)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return new FileInfo($"/proc/{process.Id}/exe").LinkTarget;
            if (OperatingSystem.IsMacOS()) return MacPath(process.Id);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception
                                              or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static string? MacPath(int processId)
    {
        var buffer = new byte[MaximumPathBytes];
        var length = ProcPidPath(processId, buffer, MaximumPathBytes);
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    [LibraryImport("libc", EntryPoint = "proc_pidpath")]
    private static partial int ProcPidPath(int processId, [Out] byte[] buffer, uint size);
}
