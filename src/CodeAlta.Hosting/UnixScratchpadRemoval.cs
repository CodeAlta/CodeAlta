using System.Buffers.Binary;
using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Catalog.Storage;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Hosting;

// Unix cannot pin a pathname, so no decision and no deletion uses one. The state root is opened
// once, proven to be ours by the inode of the lock this instance holds, and every later entry is
// looked up, opened and removed relative to the descriptor of the directory that holds it, without
// following links and without entering another mount. Pathnames only feed the enumeration of
// names and the test hooks: a renamed or swapped directory can make the enumeration incomplete,
// never redirect a deletion. Before each removal, the chain of open directories is checked to
// still lead, through "..", to the state root, where the scratchpad root must still be named
// "scratchpad": a directory moved out of the scratchpad is left.
// A file is first claimed, renamed atomically to a random name in its directory, and removed only
// when what the claim moved is the very file examined, not written since.
// Only entries older than the cutoff go: a file created or written since then is kept, and so is
// a directory created or changed since then, or one that still holds a kept entry.
//
// Residual: nothing pins a pathname against a process of the same user, so a window between the
// last check and the unlink always remains: the claim name or an ancestor directory could be
// replaced or moved right then. Using it takes a process that mutates the scratchpad at that very
// system call during startup. No CodeAlta component does: the sessions of this instance are not
// admitted before the pass ends, and legacy developer builds are excluded by their lock.
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal static class UnixScratchpadRemoval
{
    private const string LockFileName = "alta.lock";
    private const string ClaimPrefix = ".alta-cleanup-";
    private static readonly UnixFileSystem? Api = UnixFileSystem.Detect();

    /// <summary>Gets whether this operating system, architecture and C library were qualified for the removal.</summary>
    internal static bool IsSupported => Api is not null;

    /// <summary>Gets the mount id the removal reads for a directory; null where the system returns none. For tests.</summary>
    /// <exception cref="IOException">The directory cannot be opened or examined.</exception>
    internal static ulong? MountIdOf(string directory)
    {
        var api = Api ?? throw new ScratchpadSafetyException("platform-path-safety-unavailable");
        using var handle = api.OpenDirectory(directory);
        return api.Stat(handle).MountId;
    }

    internal static ScratchpadCleanupState Clear(CodeAltaInstanceProfile profile, SafeFileHandle ownership, DateTimeOffset cutoff,
        ScratchpadRemovalResult result, Action<string>? beforeOpen, Action<string>? beforeDelete, Action<string>? afterClaim,
        CancellationToken cancellationToken)
    {
        var api = Api ?? throw new ScratchpadSafetyException("platform-path-safety-unavailable");
        // Ancestors may be links (/var is one on macOS): the state root is what holds our own lock.
        using var state = api.OpenDirectory(profile.StateRoot);
        var stateIdentity = api.Stat(state);
        if (!OwnsLock(api, state, ownership))
        {
            result.Reason("instance-ownership-path-changed");
            return ScratchpadCleanupState.Skipped;
        }

        LegacyReservation? legacy = null;
        try
        {
            if (!profile.IsDeveloper)
            {
                // Legacy --dev builds still write normal scratchpads. Reserve their existing lock
                // protocol only during this worker. Never reclaim an existing lock using a PID.
                try { legacy = LegacyReservation.Acquire(api, state); }
                catch (Exception ex) when (IsFilesystemFailure(ex))
                {
                    result.Reason("legacy-developer-exclusion-unavailable");
                    return ScratchpadCleanupState.Skipped;
                }
            }

            var outcome = ClearScratchpad(api, profile, state, stateIdentity, cutoff, result, beforeOpen, beforeDelete, afterClaim, cancellationToken);
            if (legacy is not null && !legacy.Release())
            {
                result.Reason("legacy-developer-exclusion-release-failed");
                if (outcome == ScratchpadCleanupState.Completed) outcome = ScratchpadCleanupState.Partial;
            }
            return outcome;
        }
        finally
        {
            // A failure or a cancellation stops the pass: release the reservation all the same.
            if (legacy is not null && !legacy.Release()) result.Reason("legacy-developer-exclusion-release-failed");
        }
    }

    private static ScratchpadCleanupState ClearScratchpad(UnixFileSystem api, CodeAltaInstanceProfile profile, SafeFileHandle state,
        UnixStat stateIdentity, DateTimeOffset cutoff, ScratchpadRemovalResult result, Action<string>? beforeOpen,
        Action<string>? beforeDelete, Action<string>? afterClaim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle root;
        try { root = api.OpenDirectoryAt(state, "scratchpad"); }
        catch (UnixIOException ex) when (ex.Errno == UnixFileSystem.NoEntryError)
        {
            return ScratchpadCleanupState.Completed;
        }
        catch (UnixIOException ex) when (ex.Errno == api.LoopError || ex.Errno == UnixFileSystem.NotDirectoryError)
        {
            throw new ScratchpadSafetyException("linked-or-nondirectory-root-or-ancestor");
        }
        using (root)
        {
            var frame = new DirectoryFrame(Path.Combine(profile.StateRoot, "scratchpad"), null, root, api.Stat(root), stateIdentity, null);
            // The scratchpad must not be a mount of its own, a bind mount of the same file system included.
            if (!UnixFileSystem.SameMount(frame.Identity, stateIdentity)) throw new ScratchpadSafetyException("mount-point-root");
            new Walk(api, state, frame, cutoff.UtcTicks, result, beforeOpen, beforeDelete, afterClaim, cancellationToken).Run();
        }
        return result.SkippedEntries == 0 ? ScratchpadCleanupState.Completed : ScratchpadCleanupState.Partial;
    }

    // The lock this instance holds open is the regular file named alta.lock in the state root.
    private static bool OwnsLock(UnixFileSystem api, SafeFileHandle state, SafeFileHandle ownership)
    {
        var held = api.Stat(ownership);
        UnixStat named;
        try { named = api.StatAt(state, LockFileName); }
        catch (UnixIOException) { return false; }
        return held.SameEntry(named) && held.Kind == UnixEntryKind.Regular && held.Links == 1;
    }

    private static bool IsFilesystemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private sealed class Walk(UnixFileSystem api, SafeFileHandle state, DirectoryFrame root, long cutoff, ScratchpadRemovalResult result,
        Action<string>? beforeOpen, Action<string>? beforeDelete, Action<string>? afterClaim, CancellationToken cancellationToken)
    {
        internal void Run()
        {
            var frames = new Stack<DirectoryFrame>();
            frames.Push(root);
            try
            {
                while (frames.TryPeek(out var frame))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = frame.Escaped ? null : Next(frame);
                    if (entry is null)
                    {
                        frames.Pop();
                        using (frame) Finish(frame);
                        result.ReportProgress();
                        continue;
                    }

                    var (name, discoveredDirectory) = entry.Value;
                    var fullPath = Path.Join(frame.Path, name);
                    SafeFileHandle? opened = null;
                    // Counts describe examined entries, even when examining a linked or changed entry fails.
                    if (discoveredDirectory) result.ProcessedDirectories++;
                    else result.ProcessedFiles++;
                    try
                    {
                        beforeOpen?.Invoke(fullPath);
                        var found = api.StatAt(frame.Handle, name);
                        if (found.Kind == UnixEntryKind.SymbolicLink) { result.Skip("linked-entry"); continue; }
                        if (found.Kind == UnixEntryKind.Directory)
                        {
                            // A mount point is not part of the scratchpad: its file system is never entered. Only
                            // directories are compared: overlayfs reports the device of the layer for a file, not
                            // the device of its directory, and a file mounted over another one cannot be unlinked.
                            if (!UnixFileSystem.SameMount(found, root.Identity)) { result.Skip("mount-point-entry"); continue; }
                            opened = api.OpenDirectoryAt(frame.Handle, name);
                            var identity = api.Stat(opened);
                            if (!identity.SameEntry(found)) throw new IOException("The directory changed before it was opened.");
                            // Read before any child goes: removing a child changes the time of its directory.
                            frames.Push(new DirectoryFrame(fullPath, name, opened, identity, frame.Identity, frame) { Keep = identity.ChangedTicks >= cutoff });
                            opened = null;
                        }
                        else if (found.ChangedTicks >= cutoff)
                        {
                            frame.Keep = true;
                        }
                        else
                        {
                            beforeDelete?.Invoke(fullPath);
                            cancellationToken.ThrowIfCancellationRequested();
                            RemoveFile(frame, name, fullPath, found);
                            result.RemovedFiles++;
                            result.RemovedBytes += found.Size;
                        }
                    }
                    catch (Exception ex) when (IsFilesystemFailure(ex)) { result.Skip("entry-locked-denied-or-changed"); }
                    finally { opened?.Dispose(); result.ReportProgress(); }
                }
            }
            finally { while (frames.TryPop(out var frame)) frame.Dispose(); }
        }

        private ScratchpadName? Next(DirectoryFrame frame)
        {
            try
            {
                // Only names come from the pathname; each one is looked up below the frame's descriptor.
                frame.Entries ??= new FileSystemEnumerable<ScratchpadName>(frame.Path,
                    static (ref FileSystemEntry value) => new(value.FileName.ToString(), value.IsDirectory),
                    new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }).GetEnumerator();
                if (frame.Entries.MoveNext()) return frame.Entries.Current;
            }
            catch (Exception ex) when (IsFilesystemFailure(ex)) { result.Skip("directory-enumeration-unavailable"); }
            return null;
        }

        private void Finish(DirectoryFrame frame)
        {
            if (frame.Parent is null)
            {
                // The scratchpad root itself is never removed.
            }
            else if (frame.Keep || frame.Escaped)
            {
                // A directory that holds a kept entry is not empty: it is kept, not skipped.
                frame.Parent.Keep = true;
            }
            else
            {
                try
                {
                    beforeDelete?.Invoke(frame.Path);
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureContained(frame.Parent);
                    // rmdir removes only an empty directory: an entry swapped in meanwhile cannot lose content.
                    if (!api.StatAt(frame.Parent.Handle, frame.Name!).SameEntry(frame.Identity))
                        throw new IOException("The directory changed after it was examined.");
                    api.UnlinkAt(frame.Parent.Handle, frame.Name!, api.AtRemoveDir);
                    result.RemovedDirectories++;
                }
                catch (Exception ex) when (IsFilesystemFailure(ex)) { result.Skip("directory-not-empty-or-unavailable"); }
            }
        }

        // Claims the file, then removes it only when the claim holds the examined file, not written since.
        private void RemoveFile(DirectoryFrame frame, string name, string fullPath, UnixStat examined)
        {
            var claim = ClaimPrefix + RandomNumberGenerator.GetHexString(32, lowercase: true);
            api.RenameNoReplace(frame.Handle, name, claim);
            try
            {
                afterClaim?.Invoke(fullPath);
                EnsureContained(frame);
                var claimed = api.StatAt(frame.Handle, claim);
                if (!claimed.SameEntry(examined) || claimed.ModifiedTicks > examined.ModifiedTicks)
                    throw new IOException("The entry changed after it was examined.");
                // A cancellation gives the name back too.
                cancellationToken.ThrowIfCancellationRequested();
                api.UnlinkAt(frame.Handle, claim, 0);
            }
            catch
            {
                // Give the name back without replacing what may have taken it meanwhile; when it was
                // taken, the claimed file stays under the claim name. It is never removed unexamined.
                try { api.RenameNoReplace(frame.Handle, claim, name); }
                catch (Exception restore) when (IsFilesystemFailure(restore)) { }
                throw;
            }
        }

        // Every open directory from this frame up still sits in its parent, and the scratchpad root in the
        // state root under its name: a root renamed beside it is no longer the scratchpad.
        private void EnsureContained(DirectoryFrame frame)
        {
            for (var current = frame; current is not null; current = current.Parent)
            {
                if (api.StatAt(current.Handle, "..").SameEntry(current.ParentIdentity)) continue;
                frame.Escaped = true;
                throw new IOException("A directory left the scratchpad during the pass.");
            }
            if (api.StatAt(state, "scratchpad").SameEntry(root.Identity)) return;
            frame.Escaped = true;
            throw new IOException("The scratchpad was renamed during the pass.");
        }
    }

    private sealed class DirectoryFrame(string path, string? name, SafeFileHandle handle, UnixStat identity, UnixStat parentIdentity,
        DirectoryFrame? parent) : IDisposable
    {
        internal string Path { get; } = path;
        internal string? Name { get; } = name;
        internal SafeFileHandle Handle { get; } = handle;
        internal UnixStat Identity { get; } = identity;
        internal UnixStat ParentIdentity { get; } = parentIdentity;
        internal DirectoryFrame? Parent { get; } = parent;
        internal IEnumerator<ScratchpadName>? Entries { get; set; }
        internal bool Keep { get; set; }
        // Left the scratchpad: nothing more is examined or removed in it.
        internal bool Escaped { get; set; }
        public void Dispose() { try { Entries?.Dispose(); } finally { Handle.Dispose(); } }
    }

    private readonly record struct ScratchpadName(string Name, bool IsDirectory);

    // Holds dev/alta.lock, created exclusively with our PID, for the duration of the pass. A legacy
    // developer build that starts meanwhile reads a live PID and refuses to start.
    private sealed class LegacyReservation
    {
        private readonly UnixFileSystem _api;
        private readonly SafeFileHandle _directory;
        private readonly SafeFileHandle _lock;
        private readonly UnixStat _identity;
        private bool? _released;

        private LegacyReservation(UnixFileSystem api, SafeFileHandle directory, SafeFileHandle lockFile, UnixStat identity)
        {
            _api = api;
            _directory = directory;
            _lock = lockFile;
            _identity = identity;
        }

        internal static LegacyReservation Acquire(UnixFileSystem api, SafeFileHandle state)
        {
            api.MakeDirectoryAt(state, "dev", 0x1C0 /* 0700 */);
            var directory = api.OpenDirectoryAt(state, "dev");
            try
            {
                var lockFile = api.CreateExclusiveAt(directory, LockFileName, 0x180 /* 0600 */);
                UnixStat identity;
                try { identity = api.Stat(lockFile); }
                catch
                {
                    // Without its identity the new file cannot be told from another one: leave it.
                    lockFile.Dispose();
                    throw;
                }
                var reservation = new LegacyReservation(api, directory, lockFile, identity);
                try
                {
                    api.WriteAll(lockFile, Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
                    return reservation;
                }
                catch
                {
                    reservation.Release();
                    throw;
                }
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        // Removes the lock name once; false when it could not be removed. A lock left behind holds a
        // PID that exits with this process: the legacy guard reclaims it then.
        internal bool Release()
        {
            if (_released is { } released) return released;
            try
            {
                // Unix cannot delete by descriptor: the name goes only while it still is our file. A
                // swap between this check and the unlink needs a process that replaces a lock holding
                // a live PID of ours, which neither the legacy nor the current guard reclaims.
                if (!_api.StatAt(_directory, LockFileName).SameEntry(_identity)) return (_released = false).Value;
                _api.UnlinkAt(_directory, LockFileName, 0);
                return (_released = true).Value;
            }
            catch (Exception ex) when (IsFilesystemFailure(ex)) { return (_released = false).Value; }
            finally
            {
                _lock.Dispose();
                _directory.Dispose();
            }
        }
    }

    private enum UnixEntryKind { Other, Directory, Regular, SymbolicLink }

    // ModifiedTicks is the last write; ChangedTicks the latest of the last write and the creation.
    // MountId is the Linux mount id (statx STATX_MNT_ID), null where the kernel or the system has none.
    private readonly record struct UnixStat(ulong Device, ulong Inode, UnixEntryKind Kind, uint Links, long Size, long ModifiedTicks, long ChangedTicks,
        ulong? MountId = null)
    {
        internal bool SameEntry(in UnixStat other) => Device == other.Device && Inode == other.Inode;
    }

    private sealed class UnixIOException(int errno, string operation)
        : IOException(string.Create(CultureInfo.InvariantCulture, $"{operation} failed with errno {errno}."), errno)
    {
        internal int Errno { get; } = errno;
    }

    private enum StatFlavor { MacArm64, MacX64, Linux }

    // The constants of one operating system and architecture, checked against its headers.
    private sealed record UnixFileSystem(StatFlavor Flavor, int NonBlock, int Create, int Exclusive, int NoFollow,
        int Directory, int CloseOnExec, int AtFdCwd, int AtSymlinkNoFollow, int AtRemoveDir, int LoopError, int NoSystemCallError)
    {
        private const int ReadOnly = 0;
        private const int ReadWrite = 2;
        internal const int NoEntryError = 2;
        internal const int ExistsError = 17;
        internal const int NotDirectoryError = 20;
        private const int FileTypeMask = 0xF000;
        // macOS renameatx_np RENAME_EXCL, Linux renameat2 RENAME_NOREPLACE.
        private const uint MacRenameExclusive = 0x4;
        private const uint LinuxRenameNoReplace = 0x1;
        // Linux statx: AT_EMPTY_PATH, the fields asked (STATX_BASIC_STATS | STATX_BTIME | STATX_MNT_ID)
        // and those required (type, link count, write and change times, inode); the birth time and the
        // mount id are optional.
        private const int AtEmptyPath = 0x1000;
        private const uint StatxMask = 0x1FFF;
        private const uint StatxRequired = 0x1 | 0x4 | 0x40 | 0x80 | 0x100;
        private const uint StatxBirthTime = 0x800;
        private const uint StatxMountId = 0x1000;

        // Anything else (another architecture, FreeBSD, a C library without statx or renameat2:
        // glibc before 2.28, musl before 1.2.5) keeps the pass skipped.
        internal static UnixFileSystem? Detect()
        {
            var architecture = RuntimeInformation.ProcessArchitecture;
            UnixFileSystem? api = null;
            if (OperatingSystem.IsMacOS() && architecture is Architecture.Arm64 or Architecture.X64)
                api = new(architecture == Architecture.Arm64 ? StatFlavor.MacArm64 : StatFlavor.MacX64,
                    NonBlock: 0x4, Create: 0x200, Exclusive: 0x800, NoFollow: 0x100, Directory: 0x100000, CloseOnExec: 0x1000000,
                    AtFdCwd: -2, AtSymlinkNoFollow: 0x20, AtRemoveDir: 0x80, LoopError: 62, NoSystemCallError: 78);
            else if (OperatingSystem.IsLinux() && architecture is Architecture.X64 or Architecture.Arm64)
                api = new(StatFlavor.Linux, NonBlock: 0x800, Create: 0x40, Exclusive: 0x80,
                    NoFollow: architecture == Architecture.X64 ? 0x20000 : 0x8000,
                    Directory: architecture == Architecture.X64 ? 0x10000 : 0x4000,
                    CloseOnExec: 0x80000, AtFdCwd: -100, AtSymlinkNoFollow: 0x100, AtRemoveDir: 0x200, LoopError: 40, NoSystemCallError: 38);
            if (api is null) return null;
            try
            {
                using var cwd = api.CurrentDirectory();
                var probe = api.StatAt(cwd, "/");
                if (probe.Kind != UnixEntryKind.Directory || probe.Inode == 0) return null;
                // An empty name fails with ENOENT where the call exists, ENOSYS on a kernel without it.
                try { api.RenameNoReplace(cwd, string.Empty, string.Empty); }
                catch (UnixIOException ex) when (ex.Errno != api.NoSystemCallError) { }
                return api;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or IOException)
            {
                return null;
            }
        }

        private SafeFileHandle CurrentDirectory() => new((nint)AtFdCwd, ownsHandle: false);

        internal SafeFileHandle OpenDirectory(string path)
        {
            using var cwd = CurrentDirectory();
            // Follows links in the path: the caller proves the identity of what it opened.
            return Opened(OpenAt(cwd, path, ReadOnly | Directory | NonBlock | CloseOnExec), "open");
        }

        internal SafeFileHandle OpenDirectoryAt(SafeFileHandle directory, string name)
            => Opened(OpenAt(directory, name, ReadOnly | Directory | NoFollow | NonBlock | CloseOnExec), "openat");

        internal SafeFileHandle CreateExclusiveAt(SafeFileHandle directory, string name, int mode)
        {
            var flags = ReadWrite | Create | Exclusive | NoFollow | CloseOnExec;
            // openat is variadic. Apple arm64 passes variadic arguments on the stack, after the eight
            // registers: five padding arguments fill x3-x7 so that the mode lands in the first stack slot.
            var fd = Flavor == StatFlavor.MacArm64
                ? OpenAtWithModeAppleArm64(directory, name, flags, 0, 0, 0, 0, 0, mode)
                : OpenAtWithMode(directory, name, flags, mode);
            return Opened(fd, "openat");
        }

        internal void MakeDirectoryAt(SafeFileHandle directory, string name, int mode)
        {
            if (MakeDirectoryAtNative(directory, name, mode) == 0) return;
            var errno = Marshal.GetLastPInvokeError();
            if (errno != ExistsError) throw new UnixIOException(errno, "mkdirat");
        }

        internal void UnlinkAt(SafeFileHandle directory, string name, int flags)
        {
            if (UnlinkAtNative(directory, name, flags) != 0) throw Error("unlinkat");
        }

        // Renames within one directory; fails instead of replacing an entry at the new name.
        internal void RenameNoReplace(SafeFileHandle directory, string from, string to)
        {
            var status = Flavor == StatFlavor.Linux
                ? RenameAt2(directory, from, directory, to, LinuxRenameNoReplace)
                : RenameAtX(directory, from, directory, to, MacRenameExclusive);
            if (status != 0) throw Error("rename");
        }

        // Linux 5.8 and later return the mount id, which also tells a bind mount of the same file system.
        // Without it (an older kernel, macOS) only a change of device tells a mount.
        internal static bool SameMount(in UnixStat first, in UnixStat second)
            => first.MountId is { } firstMount && second.MountId is { } secondMount ? firstMount == secondMount : first.Device == second.Device;

        internal void WriteAll(SafeFileHandle file, ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var written = Write(file, ref MemoryMarshal.GetReference(bytes), bytes.Length);
                if (written <= 0) throw Error("write");
                bytes = bytes[(int)written..];
            }
        }

        internal UnixStat Stat(SafeFileHandle file)
        {
            Span<byte> buffer = stackalloc byte[256];
            ref var start = ref MemoryMarshal.GetReference(buffer);
            var status = Flavor switch
            {
                StatFlavor.MacArm64 => FStat(file, ref start),
                StatFlavor.MacX64 => FStatX64(file, ref start),
                _ => StatX(file, string.Empty, AtEmptyPath | AtSymlinkNoFollow, StatxMask, ref start),
            };
            if (status != 0) throw Error("fstat");
            return Parse(buffer);
        }

        // Never follows a link at the name.
        internal UnixStat StatAt(SafeFileHandle directory, string name)
        {
            Span<byte> buffer = stackalloc byte[256];
            ref var start = ref MemoryMarshal.GetReference(buffer);
            var status = Flavor switch
            {
                StatFlavor.MacArm64 => FStatAt(directory, name, ref start, AtSymlinkNoFollow),
                StatFlavor.MacX64 => FStatAtX64(directory, name, ref start, AtSymlinkNoFollow),
                _ => StatX(directory, name, AtSymlinkNoFollow, StatxMask, ref start),
            };
            if (status != 0) throw Error("fstatat");
            return Parse(buffer);
        }

        private UnixStat Parse(ReadOnlySpan<byte> buffer)
        {
            if (Flavor != StatFlavor.Linux)
            {
                // struct stat with 64-bit inodes (sys/stat.h): dev_t st_dev @0 (int32), st_mode @4,
                // st_nlink @6 (uint16), st_ino @8, then timespecs {sec, nsec} (int64 each):
                // st_mtimespec @48, st_ctimespec @64, st_birthtimespec @80, and st_size @96; 144 bytes.
                var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
                var modified = Ticks(BinaryPrimitives.ReadInt64LittleEndian(buffer[48..]), BinaryPrimitives.ReadInt64LittleEndian(buffer[56..]));
                var born = Ticks(BinaryPrimitives.ReadInt64LittleEndian(buffer[80..]), BinaryPrimitives.ReadInt64LittleEndian(buffer[88..]));
                return new((uint)BinaryPrimitives.ReadInt32LittleEndian(buffer),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]), Kind(mode),
                    BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]),
                    BinaryPrimitives.ReadInt64LittleEndian(buffer[96..]), modified, Math.Max(modified, born));
            }

            // struct statx (linux/stat.h), the same on every architecture: stx_mask @0, stx_nlink @16,
            // stx_mode @28 (uint16), stx_ino @32, stx_size @40, statx_timestamps {int64 sec, uint32 nsec}:
            // stx_btime @80, stx_ctime @96, stx_mtime @112; stx_dev_major @136, stx_dev_minor @140, stx_mnt_id @144 (uint64).
            var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if ((mask & StatxRequired) != StatxRequired) throw new IOException("statx did not return the fields the removal relies on.");
            var linuxMode = BinaryPrimitives.ReadUInt16LittleEndian(buffer[28..]);
            var linuxModified = Timestamp(buffer[112..]);
            // Without a birth time, the metadata change time is the conservative stand-in: it is never older.
            var second = (mask & StatxBirthTime) != 0 ? Timestamp(buffer[80..]) : Timestamp(buffer[96..]);
            var device = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(buffer[136..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(buffer[140..]);
            return new(device, BinaryPrimitives.ReadUInt64LittleEndian(buffer[32..]), Kind(linuxMode),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer[40..]),
                linuxModified, Math.Max(linuxModified, second),
                (mask & StatxMountId) != 0 ? BinaryPrimitives.ReadUInt64LittleEndian(buffer[144..]) : null);

            static long Timestamp(ReadOnlySpan<byte> value)
                => Ticks(BinaryPrimitives.ReadInt64LittleEndian(value), BinaryPrimitives.ReadUInt32LittleEndian(value[8..]));
        }

        private static UnixEntryKind Kind(ushort mode) => (mode & FileTypeMask) switch
        {
            0x4000 => UnixEntryKind.Directory,
            0x8000 => UnixEntryKind.Regular,
            0xA000 => UnixEntryKind.SymbolicLink,
            _ => UnixEntryKind.Other,
        };

        // UTC ticks; a time beyond DateTime counts as recent, so it never makes an entry removable.
        private static long Ticks(long seconds, long nanoseconds)
        {
            const long minSeconds = -62_135_596_800; // 0001-01-01 from the Unix epoch
            const long maxSeconds = 253_402_300_799; // 9999-12-31T23:59:59
            if (seconds < minSeconds || seconds > maxSeconds) return seconds < minSeconds ? long.MinValue : long.MaxValue;
            return DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond + nanoseconds / 100;
        }

        private static SafeFileHandle Opened(int fd, string operation)
            => fd >= 0 ? new SafeFileHandle(fd, ownsHandle: true) : throw Error(operation);

        private static UnixIOException Error(string operation) => new(Marshal.GetLastPInvokeError(), operation);
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtWithMode(SafeFileHandle directory, string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtWithModeAppleArm64(SafeFileHandle directory, string path, int flags,
        nint x3, nint x4, nint x5, nint x6, nint x7, nint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAtNative(SafeFileHandle directory, string path, int mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAtNative(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int RenameAtX(SafeFileHandle fromDirectory, string from, SafeFileHandle toDirectory, string to, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(SafeFileHandle fromDirectory, string from, SafeFileHandle toDirectory, string to, uint flags);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(SafeFileHandle file, ref byte buffer, nint count);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle file, ref byte buffer);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatX64(SafeFileHandle file, ref byte buffer);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAt(SafeFileHandle directory, string path, ref byte buffer, int flags);

    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static extern int FStatAtX64(SafeFileHandle directory, string path, ref byte buffer, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(SafeFileHandle directory, string path, int flags, uint mask, ref byte buffer);
}
