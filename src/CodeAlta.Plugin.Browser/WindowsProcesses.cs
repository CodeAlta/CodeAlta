using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Plugin.Browser;

/// <summary>What Windows tells of another process without reading its memory: its command line.</summary>
internal static class WindowsProcesses
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaximumSize = 1 << 20;

    /// <summary>The command line of a process, or null when the system does not give it (a process that ended, or of another user).</summary>
    [SupportedOSPlatform("windows")]
    public static string? CommandLine(int processId)
    {
        using var process = OpenProcess(QueryLimitedInformation, 0, processId);
        if (process.IsInvalid) return null;
        var size = 4096;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out var needed);
                if (status == InfoLengthMismatch && needed > size && needed <= MaximumSize)
                {
                    size = needed;
                    continue;
                }

                return status == 0 ? ReadUnicodeString(buffer, size) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    /// <summary>
    /// The UNICODE_STRING at the start of a buffer that ProcessCommandLineInformation fills: its length in bytes, then,
    /// after the alignment of a pointer, the pointer to its characters, which are in the same buffer. A pointer outside
    /// the buffer gives null, never a read elsewhere.
    /// </summary>
    internal static string? ReadUnicodeString(IntPtr buffer, int size)
    {
        if (size < 2 * IntPtr.Size) return null;
        var length = (ushort)Marshal.ReadInt16(buffer);
        if (length == 0) return string.Empty;
        var characters = Marshal.ReadIntPtr(buffer, IntPtr.Size);
        if (length % 2 != 0 || characters < buffer + 2 * IntPtr.Size || characters > buffer + size - length) return null;
        return Marshal.PtrToStringUni(characters, length / 2);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, int inheritHandle, int processId);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information, int length, out int returnLength);
}
