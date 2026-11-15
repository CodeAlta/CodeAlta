using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Core.IO;

/// <summary>
/// What the system knows a file or a folder by, whatever its path: its device and its number on that device (the inode on
/// Unix; the volume serial number and the file index on Windows). A link is not followed: it has an identity of its own.
/// A folder created at the path of another one has another identity while the first still exists.
/// </summary>
/// <remarks>
/// A file system may give the number of a deleted file to a new one (ext4 and XFS on Linux do): an identity tells two
/// files apart that exist at the same time, not a file from every one that existed before it.
/// </remarks>
/// <param name="Device">The device, or the volume serial number on Windows.</param>
/// <param name="Index">The number of the file on its device.</param>
public readonly partial record struct FileIdentity(ulong Device, ulong Index)
{
    /// <summary>
    /// Reads the identity of a file or a folder without following a link at its end.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The identity; null when there is nothing at the path, it cannot be read, or the system is not one this reads (a 32-bit process, a Unix other than Linux and macOS).</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static FileIdentity? Of(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        try
        {
            if (OperatingSystem.IsWindows()) return OfWindows(trimmed);
            if (Environment.Is64BitProcess && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return OfUnix(trimmed);
            return null;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            // A C library without lstat (glibc before 2.33 exports it under another name): not known.
            return null;
        }
    }

    // struct stat starts the same on 64-bit Linux (x64 and arm64) and macOS: the device at 0 (8 bytes on Linux, 4 on
    // macOS), the inode at 8 (8 bytes). The buffer is larger than every struct stat of those systems.
    private static unsafe FileIdentity? OfUnix(string path)
    {
        var buffer = stackalloc byte[512];
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStatInode64(path, buffer) : LStat(path, buffer);
        if (result != 0) return null;
        ulong device = OperatingSystem.IsMacOS() ? (uint)*(int*)buffer : *(ulong*)buffer;
        return new FileIdentity(device, *(ulong*)(buffer + 8));
    }

    private static FileIdentity? OfWindows(string path)
    {
        // FILE_READ_ATTRIBUTES, shared with everyone; OPEN_EXISTING; BACKUP_SEMANTICS for a folder, OPEN_REPARSE_POINT for the link itself.
        using SafeFileHandle handle = CreateFileW(path, 0x80, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information)) return null;
        return new FileIdentity(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LStat(string path, byte* buffer);

    // The 64-bit inode variant of lstat on macOS x64; on arm64 it is lstat itself.
    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LStatInode64(string path, byte* buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        // Three FILETIME values, each two 4-byte halves: 4-byte aligned, unlike a long.
        public uint CreationLow, CreationHigh, LastAccessLow, LastAccessHigh, LastWriteLow, LastWriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
