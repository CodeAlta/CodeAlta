using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Browser.Tools;

/// <summary>The path of the file a handle opened, as the system knows it: what a link in the path led to.</summary>
internal static partial class BrowserFileHandles
{
    /// <summary>The full path of an opened file, or null where the system does not tell it (macOS).</summary>
    public static string? PathOf(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows()) return WindowsPathOf(handle);
        if (OperatingSystem.IsLinux())
        {
            var descriptor = (int)handle.DangerousGetHandle();
            return new FileInfo($"/proc/self/fd/{descriptor}").LinkTarget;
        }

        return null;
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

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] ushort[] path, uint length, uint flags);
}
