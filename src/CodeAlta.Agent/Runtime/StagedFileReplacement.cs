using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Moves a file written aside onto the file it replaces, in one step: a reader finds the previous file or the new one,
/// never none. On Windows a plain move onto a file that a reader holds open is refused, even when the reader shares its
/// deletion; there the file is renamed with POSIX semantics, which replace it while it is read (the reader keeps what it
/// opened), and where the file system cannot, the plain move is tried again for a while.
/// </summary>
/// <remarks>
/// A file system filter (an antivirus scanning what a process wrote) can hold the file open without taking part in
/// its sharing: nothing then shows a reader, the file opens exclusively, it can be deleted, but no rename replaces it,
/// for as long as the filter keeps it, which can be longer than any retry. Such a file is replaced the way
/// <see cref="File.Replace(string, string, string?)"/> does it, which first moves it out of the way: a reader may then
/// find no file for a moment. A read-only file is never replaced that way.
/// </remarks>
internal static class StagedFileReplacement
{
    // How long the plain move is tried again: far beyond the time a reader holds the file.
    private static readonly TimeSpan RetryLimit = TimeSpan.FromSeconds(2);

    /// <summary>Moves <paramref name="stagedPath"/> onto <paramref name="path"/>, replacing it.</summary>
    /// <param name="stagedPath">The file written aside, in the folder of <paramref name="path"/>.</param>
    /// <param name="path">The file to replace, or to create when it does not exist.</param>
    /// <returns>A task that completes once the file is replaced.</returns>
    /// <exception cref="IOException">The file cannot be replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied, or it stayed open beyond the retries on Windows.</exception>
    /// <remarks>Not canceled: what is written may be a refreshed credential that is valid nowhere else.</remarks>
    internal static async ValueTask MoveOverAsync(string stagedPath, string path)
    {
        if (OperatingSystem.IsWindows() && TryRenameWithPosixSemantics(stagedPath, path)) return;
        var started = Stopwatch.GetTimestamp();
        var replaceTried = false;
        for (var delay = 1; ; delay = Math.Min(delay * 2, 50))
        {
            try
            {
                File.Move(stagedPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsHeldElsewhere(ex, started))
            {
                if (!replaceTried && ex is UnauthorizedAccessException)
                {
                    replaceTried = true;
                    if (TryReplaceHeldFile(stagedPath, path)) return;
                }

                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Moves <paramref name="stagedPath"/> onto <paramref name="path"/> as <see cref="MoveOverAsync"/> does, blocking the thread while it tries again.</summary>
    /// <param name="stagedPath">The file written aside, in the folder of <paramref name="path"/>.</param>
    /// <param name="path">The file to replace, or to create when it does not exist.</param>
    /// <exception cref="IOException">The file cannot be replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied, or it stayed open beyond the retries on Windows.</exception>
    /// <remarks>For a writer that holds a lock owned by its thread, which it cannot await under.</remarks>
    internal static void MoveOver(string stagedPath, string path)
        => MoveOver(stagedPath, path, renameWithPosixSemantics: OperatingSystem.IsWindows());

    // renameWithPosixSemantics: false for a test, which then reaches what follows a rename that cannot replace the
    // file, as a file system filter that holds it causes.
    internal static void MoveOver(string stagedPath, string path, bool renameWithPosixSemantics)
    {
        if (renameWithPosixSemantics && TryRenameWithPosixSemantics(stagedPath, path)) return;
        var started = Stopwatch.GetTimestamp();
        var replaceTried = false;
        for (var delay = 1; ; delay = Math.Min(delay * 2, 50))
        {
            try
            {
                File.Move(stagedPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsHeldElsewhere(ex, started))
            {
                if (!replaceTried && ex is UnauthorizedAccessException)
                {
                    replaceTried = true;
                    if (TryReplaceHeldFile(stagedPath, path)) return;
                }

                Thread.Sleep(delay);
            }
        }
    }

    // A file a filter holds outside its sharing: it is moved out of the way, then the staged file takes its name. A
    // file that does not exist, or that is read-only, is left to the move and its refusal.
    private static bool TryReplaceHeldFile(string stagedPath, string path)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return false;
            File.Replace(stagedPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Access denied for a file open elsewhere, or a sharing violation (32), within the time the move is tried again.
    private static bool IsHeldElsewhere(Exception ex, long started)
        => OperatingSystem.IsWindows()
            && (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) == 32)
            && Stopwatch.GetElapsedTime(started) < RetryLimit;

    // FileRenameInfoEx with FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS (Windows 10 1809 and
    // later, on NTFS): false where it is not supported or fails, for the plain move to try and to report what fails.
    private static bool TryRenameWithPosixSemantics(string stagedPath, string path)
    {
        const uint delete = 0x00010000, synchronize = 0x00100000;
        const uint shareAll = 0x7, openExisting = 3, openReparsePoint = 0x00200000;
        const int fileRenameInfoEx = 22;
        const uint replaceIfExists = 0x1, posixSemantics = 0x2;

        using var handle = CreateFileW(Path.GetFullPath(stagedPath), delete | synchronize, shareAll, 0, openExisting, openReparsePoint, 0);
        if (handle.IsInvalid) return false;

        // FILE_RENAME_INFO: Flags (4 bytes, aligned to a pointer), RootDirectory (a pointer), FileNameLength (4 bytes),
        // then the name in UTF-16, which is given whole: no root directory.
        var name = Verbatim(Path.GetFullPath(path));
        var nameOffset = IntPtr.Size * 2 + 4;
        var nameBytes = Encoding.Unicode.GetByteCount(name);
        var buffer = new byte[nameOffset + nameBytes + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, replaceIfExists | posixSemantics);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(IntPtr.Size * 2), (uint)nameBytes);
        Encoding.Unicode.GetBytes(name, buffer.AsSpan(nameOffset));
        return SetFileInformationByHandle(handle, fileRenameInfoEx, buffer, (uint)buffer.Length);
    }

    // The path as Windows takes it whole, beyond MAX_PATH: `\\?\C:\…`, or `\\?\UNC\server\share\…`.
    private static string Verbatim(string fullPath)
        => fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
            : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);
}
