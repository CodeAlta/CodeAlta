using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CodeAlta.Catalog.Storage;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Hosting;

// No pathname is deleted. Every path component is opened from the volume root down, with
// read access and without write/delete sharing, checked through that handle, and retained
// until child work stops. This blocks rename and reparse mutation, including path swaps
// between enumeration and opening. The final entry itself is opened without following links.
// Only entries older than the cutoff go: a file created or written since then is kept, and so is
// a directory created or changed since then, or one that still holds a kept entry.
[SupportedOSPlatform("windows")]
internal static class WindowsScratchpadRemoval
{
    private const uint ReadDirectory = 0x0001;
    private const uint ReadAttributes = 0x0080;
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint DirectoryAttribute = 0x10;
    private const uint ReparseAttribute = 0x400;

    internal static ScratchpadCleanupState Clear(CodeAltaInstanceProfile profile, SafeFileHandle ownership, DateTimeOffset cutoff,
        ScratchpadRemovalResult result, Action<string>? beforeOpen, Action<string>? beforeDelete, CancellationToken cancellationToken)
    {
        var pins = new List<SafeFileHandle>();
        FileStream? compatibilityGate = null;
        try
        {
            var stateRoot = PinAncestors(profile.StateRoot, pins);
            if (!string.Equals(FinalVolumePath(ownership), Path.Combine(stateRoot, "alta.lock"), StringComparison.OrdinalIgnoreCase))
            {
                result.Reason("instance-ownership-path-changed");
                return ScratchpadCleanupState.Skipped;
            }
            if (!profile.IsDeveloper)
            {
                // Legacy --dev builds still write normal scratchpads. Reserve their existing lock
                // protocol only during this worker. Never reclaim an existing lock using a PID.
                try
                {
                    var devRoot = Path.Combine(stateRoot, "dev");
                    Directory.CreateDirectory(devRoot);
                    pins.Add(OpenDirectory(devRoot, delete: false));
                    compatibilityGate = ReserveLegacyLock(Path.Combine(devRoot, "alta.lock"));
                }
                catch (Exception ex) when (IsFilesystemFailure(ex))
                {
                    result.Reason("legacy-developer-exclusion-unavailable");
                    return ScratchpadCleanupState.Skipped;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var scratchpad = Path.Combine(stateRoot, "scratchpad");
            SafeFileHandle root;
            try { root = OpenDirectory(scratchpad, delete: false); }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
            {
                return ScratchpadCleanupState.Completed;
            }
            pins.Add(root);
            ClearContents(scratchpad, cutoff.UtcDateTime.ToFileTimeUtc(), result, beforeOpen, beforeDelete, cancellationToken);
            return result.SkippedEntries == 0 ? ScratchpadCleanupState.Completed : ScratchpadCleanupState.Partial;
        }
        finally
        {
            // The lock is delete-pending on its exact handle before the first removal. Closing it
            // cannot delete a later file at the same pathname. Keep directory pins until afterwards.
            compatibilityGate?.Dispose();
            for (var i = pins.Count - 1; i >= 0; i--) pins[i].Dispose();
        }
    }

    private static string PinAncestors(string directory, List<SafeFileHandle> pins)
    {
        var fullPath = Path.GetFullPath(directory);
        // Network/extended-device paths have not been qualified for the local sharing contract.
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal)) throw new ScratchpadSafetyException("volume-path-safety-unavailable");
        var driveRoot = Path.GetPathRoot(fullPath)!;
        var volume = OpenDirectory(driveRoot, delete: false);
        pins.Add(volume);
        var stablePath = FinalVolumePath(volume);
        // A drive-letter pin does not pin its DOS-device mapping. Use the opened volume's GUID
        // pathname for every subsequent operation. Reject substituted drives and remote backends.
        if (!stablePath.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase)
            || stablePath.IndexOf('}') != stablePath.Length - 2
            || GetDriveType(stablePath) != 3 /* DRIVE_FIXED */)
            throw new ScratchpadSafetyException("volume-path-safety-unavailable");
        var format = new StringBuilder(32);
        if (!GetVolumeInformationByHandle(volume, null, 0, out _, out _, out _, format, (uint)format.Capacity)
            || !string.Equals(format.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new ScratchpadSafetyException("filesystem-path-safety-unavailable");
        foreach (var component in fullPath[driveRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            stablePath = Path.Combine(stablePath, component);
            pins.Add(OpenDirectory(stablePath, delete: false));
        }
        return Path.TrimEndingDirectorySeparator(FinalVolumePath(pins[^1]));
    }

    private static string FinalVolumePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1 /* VOLUME_NAME_GUID */);
        if (length == 0 || length >= buffer.Capacity) throw new ScratchpadSafetyException("volume-identity-unavailable");
        return buffer.ToString();
    }

    private static FileStream ReserveLegacyLock(string path)
    {
        var handle = CreateFile(path, GenericReadWrite | DeleteAccess, ShareRead, IntPtr.Zero, CreateNew,
            OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        FileStream? stream = null;
        try
        {
            stream = new FileStream(handle, FileAccess.ReadWrite);
            // Ordinary (not POSIX) disposition keeps the name reserved until this handle closes.
            // The legacy PID reader cannot share our DELETE access and therefore fails closed.
            MarkForDeletion(handle);
            var bytes = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return stream;
        }
        catch
        {
            if (stream is not null) stream.Dispose();
            else handle.Dispose();
            // If disposition itself failed, leave the lock untouched; never delete by pathname.
            throw;
        }
    }

    private static void ClearContents(string path, long cutoff, ScratchpadRemovalResult result, Action<string>? beforeOpen,
        Action<string>? beforeDelete, CancellationToken cancellationToken)
    {
        var frames = new Stack<DirectoryFrame>();
        frames.Push(new DirectoryFrame(path, null));
        try
        {
            while (frames.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScratchpadEntry? entry = null;
                try
                {
                    frame.Entries ??= new FileSystemEnumerable<ScratchpadEntry>(frame.Path,
                        static (ref FileSystemEntry value) => new(value.ToFullPath(), value.IsDirectory),
                        new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }).GetEnumerator();
                    if (frame.Entries.MoveNext()) entry = frame.Entries.Current;
                }
                catch (Exception ex) when (IsFilesystemFailure(ex)) { result.Skip("directory-enumeration-unavailable"); }

                if (entry is null)
                {
                    frames.Pop();
                    using (frame)
                    {
                        if (frame.Keep)
                        {
                            // A directory that holds a kept entry is not empty: it is kept, not skipped.
                            if (frames.TryPeek(out var parent)) parent.Keep = true;
                        }
                        else if (frame.Handle is not null)
                        {
                            try
                            {
                                beforeDelete?.Invoke(frame.Path);
                                cancellationToken.ThrowIfCancellationRequested();
                                MarkForDeletion(frame.Handle);
                                result.RemovedDirectories++;
                            }
                            catch (Exception ex) when (IsFilesystemFailure(ex)) { result.Skip("directory-not-empty-or-unavailable"); }
                        }
                    }
                    result.ReportProgress();
                    continue;
                }

                SafeFileHandle? handle = null;
                var discovered = entry.Value;
                // Counts describe examined entries, even when opening a locked/linked entry fails.
                // Deletion still relies exclusively on the pinned handle's attributes below.
                if (discovered.IsDirectory) result.ProcessedDirectories++;
                else result.ProcessedFiles++;
                try
                {
                    beforeOpen?.Invoke(discovered.Path);
                    handle = Open(discovered.Path, delete: true);
                    var attributes = Attributes(handle);
                    if ((attributes & ReparseAttribute) != 0) { result.Skip("linked-entry"); continue; }
                    // Read before any child goes: removing a child changes the time of its directory.
                    var recent = LastChanged(handle) >= cutoff;
                    if ((attributes & DirectoryAttribute) != 0)
                    {
                        frames.Push(new DirectoryFrame(discovered.Path, handle) { Keep = recent });
                        handle = null;
                    }
                    else if (recent)
                    {
                        frame.Keep = true;
                    }
                    else
                    {
                        beforeDelete?.Invoke(discovered.Path);
                        cancellationToken.ThrowIfCancellationRequested();
                        // Read on the handle that deletes it: the size of the file that goes, whatever its name became.
                        var size = Length(handle);
                        MarkForDeletion(handle);
                        result.RemovedFiles++;
                        result.RemovedBytes += size;
                    }
                }
                catch (Exception ex) when (IsFilesystemFailure(ex)) { result.Skip("entry-locked-denied-or-changed"); }
                finally { handle?.Dispose(); result.ReportProgress(); }
            }
        }
        finally { while (frames.TryPop(out var frame)) frame.Dispose(); }
    }

    // The length of an open file; 0 when it does not read, which only makes the size shown smaller.
    private static long Length(SafeFileHandle handle)
    {
        try { return RandomAccess.GetLength(handle); }
        catch (Exception ex) when (IsFilesystemFailure(ex)) { return 0; }
    }

    private sealed class DirectoryFrame(string path, SafeFileHandle? handle) : IDisposable
    {
        internal string Path { get; } = path;
        internal SafeFileHandle? Handle { get; } = handle;
        internal IEnumerator<ScratchpadEntry>? Entries { get; set; }
        internal bool Keep { get; set; }
        public void Dispose() { try { Entries?.Dispose(); } finally { Handle?.Dispose(); } }
    }

    private readonly record struct ScratchpadEntry(string Path, bool IsDirectory);

    private static SafeFileHandle OpenDirectory(string path, bool delete)
    {
        var handle = Open(path, delete);
        try
        {
            var attributes = Attributes(handle);
            if ((attributes & (DirectoryAttribute | ReparseAttribute)) != DirectoryAttribute)
                throw new ScratchpadSafetyException("linked-or-nondirectory-root-or-ancestor");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle Open(string path, bool delete)
    {
        var handle = CreateFile(path, ReadAttributes | ReadDirectory | (delete ? DeleteAccess : 0), ShareRead,
            IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static uint Attributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Attributes;
    }

    // The latest of the creation and the last write, as a UTC file time. A copied file keeps the
    // write time of its source but is created anew. Access and metadata-change times do not count.
    private static long LastChanged(SafeFileHandle handle)
    {
        if (!GetFileBasicInformation(handle, 0 /* FileBasicInfo */, out var info, BasicInfo.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Math.Max(info.CreationTime, info.LastWriteTime);
    }

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        var delete = 1;
        if (!SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, ref delete, sizeof(int)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static bool IsFilesystemFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or Win32Exception;

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { internal uint Attributes; internal uint ReparseTag; }

    // FILE_BASIC_INFO: four LARGE_INTEGER times, then the attributes, padded to 8 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        internal const uint Size = 40;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
        internal uint Padding;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string root);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, StringBuilder? volumeName,
        uint volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint flags,
        StringBuilder fileSystemName, uint fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out AttributeTagInfo information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileBasicInformation(SafeFileHandle handle, int informationClass,
        out BasicInfo information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref int information, int size);
}
