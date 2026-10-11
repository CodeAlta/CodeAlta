using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Agent.Runtime;

// Retirement must not release a read lock and then delete by path: that could delete a replacement generation.
// Windows supports disposition on the very handle we digested, with replacement/writes denied for its lifetime.
// No equivalent Unix directory-relative implementation has been proved here; those hosts fail closed.
internal sealed class RetentionJournalLease : IDisposable
{
    private readonly List<SafeFileHandle> _ancestors;
    internal FileStream Stream { get; }
    internal string Path { get; }
    internal string Identity { get; }
    internal DateTimeOffset LastWriteUtc { get; }

    private RetentionJournalLease(List<SafeFileHandle> ancestors, SafeFileHandle file, string path, FileInformation identity)
    {
        _ancestors = ancestors;
        Path = path;
        Identity = $"{identity.VolumeSerialNumber:x8}:{identity.FileIndexHigh:x8}{identity.FileIndexLow:x8}";
        LastWriteUtc = DateTimeOffset.FromFileTime(((long)(uint)identity.LastWriteTime.dwHighDateTime << 32)
            | (uint)identity.LastWriteTime.dwLowDateTime).ToUniversalTime();
        Stream = new FileStream(file, FileAccess.Read);
    }

    internal static RetentionJournalLease Open(AgentRuntimePathLayout layout, string sessionId, DateTimeOffset createdAt)
        => Open(layout, sessionId, createdAt, null);

    // The observer allows an adversary test between ancestor acquisition and opening its next descendant.
    internal static RetentionJournalLease Open(AgentRuntimePathLayout layout, string sessionId, DateTimeOffset createdAt, Action<string>? ancestorClaimed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId is "." or ".." || sessionId.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || sessionId.Contains('/') || sessionId.Contains('\\') || sessionId != sessionId.Trim())
            throw new ArgumentException("Session ID must be an exact ordinary filename segment.", nameof(sessionId));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Exact-source retirement is not supported on this filesystem platform.");
        var path = System.IO.Path.GetFullPath(layout.GetSessionFilePath(sessionId, createdAt));
        return OpenExactFile(path, ancestorClaimed);
    }

    private static RetentionJournalLease OpenExactFile(string path, Action<string>? ancestorClaimed = null)
    {
        var ancestors = new List<SafeFileHandle>();
        SafeFileHandle? file = null;
        try
        {
            var chain = new Stack<string>();
            for (var directory = System.IO.Path.GetDirectoryName(path); directory is not null; directory = System.IO.Path.GetDirectoryName(directory))
                chain.Push(directory);
            while (chain.TryPop(out var directory))
            {
                // Keep each verified ancestor from being renamed/replaced until every child handle is closed.
                // READ_ATTRIBUTES alone does not participate in Windows read/write/delete share arbitration.
                // FILE_LIST_DIRECTORY provides read access so a pre-existing/future reparse writer or rename
                // cannot bypass the claim during the ancestor-to-descendant construction window.
                var handle = OpenHandle(directory, 0x81, share: 1);
                ancestors.Add(handle);
                var info = Information(handle);
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                    throw new IOException("Unsafe retention ancestor.");
                ancestorClaimed?.Invoke(directory);
            }
            file = OpenHandle(path, 0x80010000, share: 1); // GENERIC_READ | DELETE; deny writers and replacement
            var identity = Information(file);
            if ((identity.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || identity.NumberOfLinks != 1)
                throw new IOException("Unsafe retention journal (link or nonordinary file).");
            var lease = new RetentionJournalLease(ancestors, file, path, identity);
            file = null;
            return lease;
        }
        catch
        {
            file?.Dispose();
            foreach (var ancestor in ancestors.AsEnumerable().Reverse()) ancestor.Dispose();
            throw;
        }
    }

    // Only the owning layout supplies these paths. Capture before deletion; never sweep after a crash or follow
    // paths embedded in the journal. Nested/unsafe/locked entries are deliberately left as a partial outcome.
    internal RetentionArtifacts CaptureArtifacts(AgentRuntimePathLayout layout, string sessionId)
    {
        var handles = new List<RetentionJournalLease>();
        var partial = false;
        var attachmentRoot = System.IO.Path.ChangeExtension(Path, ".attachments");
        try
        {
            var attachmentAttributes = AttributesOrMissing(attachmentRoot);
            if (attachmentAttributes is { } attributes && (attributes & FileAttributes.Directory) != 0)
            {
                using var directory = OpenHandle(attachmentRoot, 0x81, 1);
                if ((Information(directory).Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    partial = true;
                else
                {
                    foreach (var artifact in Directory.EnumerateFileSystemEntries(attachmentRoot))
                    {
                        if (handles.Count == 256) { partial = true; break; }
                        TryCapture(artifact);
                    }
                }
            }
            else if (attachmentAttributes is not null) partial = true;
            var trace = layout.GetSessionTraceFilePath(sessionId);
            if (AttributesOrMissing(trace) is not null) TryCapture(trace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            partial = true;
        }
        return new(handles, partial);

        void TryCapture(string path)
        {
            try { handles.Add(OpenExactFile(System.IO.Path.GetFullPath(path))); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { partial = true; }
        }

        static FileAttributes? AttributesOrMissing(string path)
        {
            try { return System.IO.File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }

    internal void Delete()
    {
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, ref disposition, 1))
            throw new IOException("Could not retire the verified journal handle.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public void Dispose()
    {
        Stream.Dispose();
        foreach (var ancestor in _ancestors.AsEnumerable().Reverse()) ancestor.Dispose();
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint share)
    {
        var handle = CreateFileW(path, access, share, 0, 3, 0x02200000, 0); // OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("Cannot exclusively claim the retention path.", new Win32Exception(error));
    }

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("Cannot verify retention file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition information, uint size);
}

internal sealed class RetentionArtifacts(List<RetentionJournalLease> files, bool partial) : IDisposable
{
    internal bool DeleteCaptured()
    {
        foreach (var file in files)
        {
            try { file.Delete(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { partial = true; }
        }
        return partial;
    }

    public void Dispose()
    {
        foreach (var file in files) file.Dispose();
    }
}
