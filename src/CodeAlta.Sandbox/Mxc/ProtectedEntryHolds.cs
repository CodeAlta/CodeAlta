using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Sandbox.Mxc;

/// <summary>
/// The protected entries of a policy held open on Windows while its container runs, so that the sandbox cannot replace,
/// delete or move them.
/// </summary>
/// <remarks>
/// A read-only grant stops a write to an entry, not a rename in its writable folder that replaces it (git rewrites
/// <c>.git/config</c> through <c>config.lock</c>), nor the move of a folder above it, which takes the entry out of its
/// path to be changed and brought back, or brings in one that is denied while it does not exist. Windows refuses to
/// delete, rename or replace what a process holds open without sharing its deletion, whatever its permissions: each
/// protected entry that exists and each folder between it and the writable folder that holds it are held that way, read
/// and write shared. The programs of the user cannot replace them either until the container ends; CodeAlta's own writes
/// go in place (<c>SafeFile</c>). On macOS and Linux nothing is held: Seatbelt refuses the replacement by path, not the
/// move of a folder above the entry, which mxc cannot express there.
/// </remarks>
internal sealed partial class ProtectedEntryHolds : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    // A folder opens only with backup semantics; a link is held itself, not what it names.
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;

    // The waits for an entry another program opens a moment without sharing (an antivirus), about a quarter of a second.
    private static ReadOnlySpan<int> SharingWaits => [10, 20, 40, 80, 100];

    private readonly List<SafeFileHandle> _handles;

    private ProtectedEntryHolds(List<SafeFileHandle> handles) => _handles = handles;

    /// <summary>Holds the entries of a policy, after <see cref="MxcRequests.Prepare"/> made what it makes.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The holds, to dispose once the container ended.</returns>
    /// <exception cref="SandboxUnavailableException">An entry cannot be held: the sandbox could replace it.</exception>
    public static ProtectedEntryHolds Take(SandboxPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var handles = new List<SafeFileHandle>();
        if (!OperatingSystem.IsWindows()) return new ProtectedEntryHolds(handles);
        try
        {
            foreach (var path in PathsOf(policy))
            {
                if (Open(path) is { } handle) handles.Add(handle);
            }
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }

        return new ProtectedEntryHolds(handles);
    }

    /// <summary>
    /// Gets the paths a policy holds: each folder between a writable folder and a protected entry inside it, then the
    /// entry, when they exist. An entry outside every writable folder cannot be replaced by the sandbox.
    /// </summary>
    internal static IReadOnlyList<string> PathsOf(SandboxPolicy policy)
    {
        var comparer = SandboxPolicyBuilder.PathComparer;
        var paths = new List<string>();
        foreach (var entry in policy.Protected)
        {
            var root = policy.ReadWrite.Where(grant => !comparer.Equals(grant, entry) && SandboxPolicyBuilder.IsInside(grant, entry)).MaxBy(static grant => grant.Length);
            if (root is null) continue;
            var folders = new List<string>();
            for (var folder = Path.GetDirectoryName(entry); folder is not null && !comparer.Equals(folder, root); folder = Path.GetDirectoryName(folder))
            {
                if (Directory.Exists(folder)) folders.Add(folder);
            }

            folders.Reverse();
            paths.AddRange(folders);
            if (File.Exists(entry) || Directory.Exists(entry)) paths.Add(entry);
        }

        return [.. paths.Distinct(comparer)];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var handle in _handles) handle.Dispose();
        _handles.Clear();
    }

    private static SafeFileHandle? Open(string path)
    {
        foreach (var milliseconds in SharingWaits)
        {
            var handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            // Gone meanwhile: nothing to hold, and the sandbox cannot create it in a protected path.
            if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
            if (error != ErrorSharingViolation) throw Refused(path, error);
            Thread.Sleep(milliseconds);
        }

        throw Refused(path, ErrorSharingViolation);
    }

    private static SandboxUnavailableException Refused(string path, int error)
        => new($"The sandbox did not start: CodeAlta cannot hold '{path}' ({Marshal.GetPInvokeErrorMessage(error)}), which the sandbox could then replace.");

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, nint templateFile);
}
