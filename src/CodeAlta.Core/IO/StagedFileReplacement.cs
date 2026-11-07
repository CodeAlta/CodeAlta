using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Core.IO;

/// <summary>
/// Moves a file written aside onto the file it replaces, in one step: a reader finds the previous file or the new one,
/// never none. On Windows a plain move onto a file that a reader holds open is refused, even when the reader shares its
/// deletion; there the file is renamed with POSIX semantics, which replace it while it is read (the reader keeps what it
/// opened), and where the file system cannot, the plain move is tried again for a while.
/// </summary>
/// <remarks>
/// <para>
/// On Windows even that rename can leave no file for a moment, rarely (about one replacement in ten thousand, for tens of
/// milliseconds): the previous file is gone, the staged one not yet in its place. A reader for which a missing file
/// means something (a credential read as a sign-out) opens it with <see cref="OpenReplacedFileAsync"/>, which waits for
/// the new one while a staged file shows a write in progress. Such a writer names its staged file with
/// <see cref="StagedPathOf"/>.
/// </para>
/// <para>
/// A file system filter (an antivirus scanning what a process wrote) can hold the file open without taking part in
/// its sharing: nothing then shows a reader, the file opens exclusively, it can be deleted, but no rename replaces it,
/// for as long as the filter keeps it, which can be longer than any retry. Such a file is replaced the way
/// <see cref="File.Replace(string, string, string?)"/> does it, which first moves it out of the way: a reader may then
/// find no file for a moment. A read-only file is never replaced that way.
/// </para>
/// </remarks>
public static class StagedFileReplacement
{
    // How long the plain move is tried again: far beyond the time a reader holds the file.
    private static readonly TimeSpan RetryLimit = TimeSpan.FromSeconds(2);

    // How recent a staged file is for a write in progress: older, it was left by a write that never ended.
    private static readonly TimeSpan StagedFileLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets a new name for the file a write stages beside <paramref name="path"/>: <c>&lt;file name&gt;.&lt;guid&gt;.tmp</c> in
    /// its folder, which <see cref="OpenReplacedFileAsync"/> takes for a write in progress, and which no other write uses.
    /// </summary>
    /// <param name="path">The file the write replaces.</param>
    /// <returns>The path of the staged file.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static string StagedPathOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    }

    /// <summary>
    /// Opens for reading a file that a write replaces with <see cref="MoveOverAsync"/>, from a staged file named
    /// <c>&lt;file name&gt;.&lt;anything&gt;.tmp</c> in its folder, or returns <see langword="null"/> when there is no file.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels the wait for a file being replaced.</param>
    /// <returns>The file, opened to be read while a writer replaces it (the reader keeps what it opened), or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while the file was being replaced.</exception>
    /// <remarks>
    /// On Windows the open is tried again for up to two seconds: for a missing file while a recent staged file shows a
    /// replacement in progress (or once it is back), and for an access denied or a sharing violation, which a replacement
    /// causes for a moment, as the writer tries its move again. A file missing without a write in progress is none at once.
    /// </remarks>
    public static async ValueTask<FileStream?> OpenReplacedFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var started = Stopwatch.GetTimestamp();
        for (var delay = 1; ; delay = Math.Min(delay * 2, 50))
        {
            if (TryOpenReplacedFile(path, started, useAsync: true, out var stream)) return stream;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens for reading a file that a write replaces, as <see cref="OpenReplacedFileAsync"/> does, blocking the thread
    /// while it waits for a file being replaced.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file, opened to be read while a writer replaces it (the reader keeps what it opened), or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <remarks>For a reader that holds a lock owned by its thread, which it cannot await under.</remarks>
    public static FileStream? OpenReplacedFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var started = Stopwatch.GetTimestamp();
        for (var delay = 1; ; delay = Math.Min(delay * 2, 50))
        {
            if (TryOpenReplacedFile(path, started, useAsync: false, out var stream)) return stream;
            Thread.Sleep(delay);
        }
    }

    // True once the open is decided: the file, or null for none. False to try again, in the moment of a replacement.
    private static bool TryOpenReplacedFile(string path, long started, bool useAsync, out FileStream? stream)
    {
        stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A file back already, or a staged file still there: the open came in the moment of a replacement.
            return !CanWaitForReplacement(started) || !(IsBeingReplaced(path) || File.Exists(path));
        }
        catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) == 32)
            && CanWaitForReplacement(started) && !Directory.Exists(path))
        {
            return false;
        }
    }

    private static bool CanWaitForReplacement(long started)
        => OperatingSystem.IsWindows() && Stopwatch.GetElapsedTime(started) < RetryLimit;

    /// <summary>Moves <paramref name="stagedPath"/> onto <paramref name="path"/>, replacing it.</summary>
    /// <param name="stagedPath">The file written aside, in the folder of <paramref name="path"/>.</param>
    /// <param name="path">The file to replace, or to create when it does not exist.</param>
    /// <returns>A task that completes once the file is replaced.</returns>
    /// <exception cref="ArgumentException"><paramref name="stagedPath"/> or <paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied, or it stayed open beyond the retries on Windows.</exception>
    /// <remarks>Not canceled: what is written may be a refreshed credential that is valid nowhere else.</remarks>
    public static async ValueTask MoveOverAsync(string stagedPath, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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
            catch (Exception ex) when (IsHeldElsewhere(ex, path, started))
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
    /// <exception cref="ArgumentException"><paramref name="stagedPath"/> or <paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied, or it stayed open beyond the retries on Windows.</exception>
    /// <remarks>For a writer that holds a lock owned by its thread, which it cannot await under.</remarks>
    public static void MoveOver(string stagedPath, string path)
        => MoveOver(stagedPath, path, renameWithPosixSemantics: OperatingSystem.IsWindows());

    // renameWithPosixSemantics: false for a test, which then reaches what follows a rename that cannot replace the
    // file, as a file system filter that holds it causes.
    internal static void MoveOver(string stagedPath, string path, bool renameWithPosixSemantics)
        => MoveOver(stagedPath, path, renameWithPosixSemantics, moveHeldFileAside: true);

    /// <summary>
    /// Moves <paramref name="stagedPath"/> onto <paramref name="path"/> as <see cref="MoveOver(string, string)"/> does, but never
    /// moves the file out of the way: beyond the moment a rename takes on Windows, a reader finds the previous file or the new
    /// one. A file a filter holds beyond the retries is left as it is, and the write fails.
    /// </summary>
    /// <param name="stagedPath">The file written aside, in the folder of <paramref name="path"/>.</param>
    /// <param name="path">The file to replace, or to create when it does not exist.</param>
    /// <exception cref="ArgumentException"><paramref name="stagedPath"/> or <paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied, or it stayed open beyond the retries on Windows.</exception>
    /// <remarks>For a store whose readers take a missing file for an empty one: the secrets, the versions a machine took.</remarks>
    public static void MoveOverWithoutGap(string stagedPath, string path)
        => MoveOver(stagedPath, path, OperatingSystem.IsWindows(), moveHeldFileAside: false);

    internal static void MoveOverWithoutGap(string stagedPath, string path, bool renameWithPosixSemantics)
        => MoveOver(stagedPath, path, renameWithPosixSemantics, moveHeldFileAside: false);

    private static void MoveOver(string stagedPath, string path, bool renameWithPosixSemantics, bool moveHeldFileAside)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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
            catch (Exception ex) when (IsHeldElsewhere(ex, path, started))
            {
                if (moveHeldFileAside && !replaceTried && ex is UnauthorizedAccessException)
                {
                    replaceTried = true;
                    if (TryReplaceHeldFile(stagedPath, path)) return;
                }

                Thread.Sleep(delay);
            }
        }
    }

    private static bool IsBeingReplaced(string path)
    {
        try
        {
            var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var now = DateTime.UtcNow;
            foreach (var staged in folder.EnumerateFiles(Path.GetFileName(path) + ".*.tmp"))
            {
                if (now - staged.LastWriteTimeUtc < StagedFileLifetime) return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
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

    // Access denied for a file open elsewhere, or a sharing violation (32), within the time the move is tried again. A folder in
    // the place of the file, or a read-only file, is denied for good: no reader lets it go, and it is refused at once.
    private static bool IsHeldElsewhere(Exception ex, string path, long started)
        => OperatingSystem.IsWindows()
            && (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) == 32)
            && Stopwatch.GetElapsedTime(started) < RetryLimit
            && !IsDeniedForGood(path);

    private static bool IsDeniedForGood(string path)
    {
        try
        {
            return Directory.Exists(path) || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Its attributes do not read: it may be held, and is tried again.
            return false;
        }
    }

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
