using System.Runtime.InteropServices;

namespace CodeAlta.Catalog;

/// <summary>
/// Reads a small file with the C library, outside Windows. A .NET read takes a shared advisory lock (<c>flock</c>) on Unix,
/// which waits on the exclusive one a session lease holds: this read takes none, so the holder can be named.
/// </summary>
internal static class UnixFileContent
{
    private const int ReadOnly = 0; // O_RDONLY, 0 on Linux and macOS.
    private const int Interrupted = 4; // EINTR, 4 on Linux and macOS.

    /// <summary>Reads a file of at most <paramref name="maximumBytes"/> bytes.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="maximumBytes">The most bytes the file may hold.</param>
    /// <param name="content">The bytes of the file; empty when it cannot be read.</param>
    /// <returns><see langword="true"/> when the file was read whole; <see langword="false"/> when it is missing, cannot be read, or is larger.</returns>
    internal static bool TryRead(string path, int maximumBytes, out byte[] content)
    {
        content = [];
        var descriptor = Open(path, ReadOnly);
        if (descriptor < 0) return false;
        try
        {
            var buffer = new byte[maximumBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = Read(descriptor, ref buffer[total], buffer.Length - total);
                if (read < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Interrupted) continue;
                    return false;
                }

                if (read == 0) break;
                total += (int)read;
            }

            if (total > maximumBytes) return false;
            content = buffer[..total];
            return true;
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int descriptor, ref byte buffer, nint count);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
