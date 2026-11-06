using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Browser.Tools;

/// <summary>The path of the file a handle opened, as the system knows it: what a link in the path led to.</summary>
internal static partial class BrowserFileHandles
{
    // The struct vnode_fdinfowithpath of <sys/proc_info.h> that proc_pidfdinfo fills for PROC_PIDFDVNODEPATHINFO: a
    // proc_fileinfo (24 bytes) and a vnode_info (152 bytes), then the path (MAXPATHLEN bytes).
    private const int MacVnodePathInfo = 2;
    private const int MacVnodePathInfoSize = 1200;
    private const int MacPathOffset = 176;

    /// <summary>The full path of an opened file, or null where the system does not tell it.</summary>
    public static string? PathOf(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows()) return WindowsPathOf(handle);
        if (OperatingSystem.IsLinux())
        {
            var descriptor = (int)handle.DangerousGetHandle();
            return new FileInfo($"/proc/self/fd/{descriptor}").LinkTarget;
        }

        return OperatingSystem.IsMacOS() ? MacPathOf(handle) : null;
    }

    private static string? MacPathOf(SafeFileHandle handle)
    {
        var buffer = new byte[MacVnodePathInfoSize];
        int written;
        try
        {
            written = proc_pidfdinfo(Environment.ProcessId, (int)handle.DangerousGetHandle(), MacVnodePathInfo, buffer, buffer.Length);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        // Any other size is a layout this code does not know: the caller then resolves the path again.
        if (written != buffer.Length) return null;
        var path = buffer.AsSpan(MacPathOffset);
        var end = path.IndexOf((byte)0);
        if (end <= 0) return null;
        var text = Encoding.UTF8.GetString(path[..end]);
        return text.StartsWith('/') ? text : null;
    }

    private static string? WindowsPathOf(SafeFileHandle handle)
    {
        var buffer = new ushort[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        var path = new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, (int)length)));
        // \\?\C:\folder or \\?\UNC\server\share\folder.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("libproc", ExactSpelling = true)]
    private static extern int proc_pidfdinfo(int pid, int fd, int flavor, [Out] byte[] buffer, int size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] ushort[] path, uint length, uint flags);
}
