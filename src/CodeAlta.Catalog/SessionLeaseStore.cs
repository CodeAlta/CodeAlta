using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// The leases of the sessions, across processes: whoever runs a session (the command line, CodeAlta Desktop or TUI)
/// holds its lease, <c>&lt;session-id&gt;.lease</c> in one flat folder, so that a second process cannot run it too.
/// </summary>
/// <remarks>
/// <para>
/// A lease is an open handle on its file, held as long as the lease, with a small JSON content naming the holder. The
/// handle is the truth, not the content: the operating system closes it when the process ends, so the lease of a process
/// that crashed can be taken again at once, without a check of process ids and without cleanup. A file without a holder
/// is free, whatever it says.
/// </para>
/// <para>
/// On Windows the holder opens the file for writing and shares reading only: no other handle can open it for writing,
/// in this process or another, and everyone can read who holds it. Outside Windows, .NET emulates sharing with advisory
/// <c>flock</c> locks between the .NET processes, and a writer that shares reading takes a shared lock only, which a second
/// writer shares (checked on Linux): the holder shares nothing, which takes the exclusive lock, and the content is read
/// with the C library, which takes no lock. Locks are per open file, so a second lease of this process for the same
/// session fails like a foreign one. The lock is advisory: only .NET processes (and others that use <c>flock</c>) see it,
/// and network file systems may not carry it between computers.
/// </para>
/// <para>
/// Release deletes the file, best effort. Outside Windows the name is removed while the lock is still held, and a new
/// holder checks that the file it locked is still the one of the name, so two holders never hold two files of one name.
/// </para>
/// </remarks>
public sealed class SessionLeaseStore
{
    private const string Extension = ".lease";
    private const int MaximumContentBytes = 4096;

    // A probe of a reader or the release of a holder blocks an opening for a moment: an acquisition tries a few times.
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(15);

    private readonly TimeProvider _time;
    private readonly Lazy<DateTimeOffset?> _processStart = new(ReadProcessStart);

    /// <summary>Initializes the store of the leases of an instance's state root (<see cref="CatalogOptions.SessionLeasesRoot"/>).</summary>
    /// <param name="options">The catalog options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The state root is empty.</exception>
    public SessionLeaseStore(CatalogOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).SessionLeasesRoot)
    {
    }

    /// <summary>Initializes the store of the leases of a folder.</summary>
    /// <param name="leasesDirectory">The folder of the lease files.</param>
    /// <exception cref="ArgumentException"><paramref name="leasesDirectory"/> is empty or not a valid path.</exception>
    public SessionLeaseStore(string leasesDirectory)
        : this(leasesDirectory, TimeProvider.System)
    {
    }

    /// <summary>Initializes the store of the leases of a folder, with its clock.</summary>
    /// <param name="leasesDirectory">The folder of the lease files.</param>
    /// <param name="time">The clock of <see cref="SessionLeaseHolder.AcquiredUtc"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="leasesDirectory"/> is empty or not a valid path.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    public SessionLeaseStore(string leasesDirectory, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leasesDirectory);
        ArgumentNullException.ThrowIfNull(time);
        LeasesDirectory = Path.GetFullPath(leasesDirectory);
        _time = time;
    }

    /// <summary>Gets the folder of the lease files: one listing shows every leased session.</summary>
    public string LeasesDirectory { get; }

    /// <summary>Gets whether a session id can have a lease: 1 to 128 characters, without path characters.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool IsValidSessionId(string? sessionId) => SessionFileName.IsValid(sessionId);

    /// <summary>Gets the path of the lease file of a session.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is not valid (<see cref="IsValidSessionId"/>).</exception>
    public string GetLeasePath(string sessionId)
    {
        SessionFileName.Validate(sessionId, nameof(sessionId));
        return Path.Combine(LeasesDirectory, sessionId + Extension);
    }

    /// <summary>
    /// Takes the lease of a session, unless a process holds it: another one, or this one through another lease.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="owner">Who takes it.</param>
    /// <returns>The lease, which the caller disposes to release it; <see langword="null"/> when it is held.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is not valid (<see cref="IsValidSessionId"/>).</exception>
    /// <exception cref="IOException">The folder or the file cannot be created or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the folder is denied.</exception>
    public SessionLease? TryAcquire(string sessionId, SessionLeaseOwner owner)
    {
        var path = GetLeasePath(sessionId);
        ArgumentNullException.ThrowIfNull(owner);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(RetryDelay);
            Directory.CreateDirectory(LeasesDirectory);
            if (TryOpen(path) is not { } stream) continue;
            try
            {
                var holder = new SessionLeaseHolder(sessionId, owner.Kind, Environment.ProcessId, _processStart.Value, owner.InstanceId, _time.GetUtcNow());
                var content = JsonSerializer.SerializeToUtf8Bytes(LeaseContent.From(holder), SessionFilesJsonContext.Default.LeaseContent);
                stream.SetLength(0);
                stream.Write(content);
                stream.Flush(flushToDisk: true);
                if (!OperatingSystem.IsWindows() && !(UnixFileContent.TryRead(path, MaximumContentBytes, out var named) && named.AsSpan().SequenceEqual(content)))
                {
                    // The file was released and its name removed between the opening and the lock: the lock is on a
                    // file nobody can find. The next attempt opens the file of the name.
                    stream.Dispose();
                    continue;
                }

                return new SessionLease(path, stream, holder);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads who holds the lease of a session, without disturbing the holder.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>
    /// The holder; <see langword="null"/> when the lease is free: no file, or a file nobody holds open.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is not valid (<see cref="IsValidSessionId"/>).</exception>
    /// <exception cref="IOException">The file cannot be inspected.</exception>
    public SessionLeaseHolder? TryReadHolder(string sessionId)
    {
        var path = GetLeasePath(sessionId);
        if (!IsHeld(path)) return null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(RetryDelay);
            if (TryReadContent(path) is { } content && content.ToHolder(sessionId) is { } holder) return holder;
        }

        // Held, but its holder has not written it yet, or it cannot be read.
        return new SessionLeaseHolder(sessionId, null, null, null, null, null);
    }

    /// <summary>Lists the leases held now, by this process and the others.</summary>
    /// <returns>The holders; a file whose name is not a session id, or that nobody holds, is left out.</returns>
    /// <remarks>A lease of this process has <see cref="SessionLeaseHolder.ProcessId"/> equal to <see cref="Environment.ProcessId"/>.</remarks>
    /// <exception cref="IOException">The folder cannot be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the folder is denied.</exception>
    public IReadOnlyList<SessionLeaseHolder> ListHeld()
    {
        if (!Directory.Exists(LeasesDirectory)) return [];
        var held = new List<SessionLeaseHolder>();
        foreach (var path in Directory.EnumerateFiles(LeasesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase)) continue;
            var sessionId = Path.GetFileNameWithoutExtension(path);
            if (!SessionFileName.IsValid(sessionId)) continue;
            if (TryReadHolder(sessionId) is { } holder) held.Add(holder);
        }

        return held;
    }

    // The handle of a lease, or null when the file is held: by a holder, or for a moment by a probe or a release.
    private static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None, bufferSize: 1, FileOptions.None);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && File.Exists(path))
        {
            // The file of a released lease is being deleted while a reader still has it open.
            return null;
        }
    }

    // Whether a process holds the file open for writing. The probe opens it for reading only, sharing reading: on Windows
    // that fails while a handle writes; outside Windows it takes a shared lock, which fails while the exclusive lock of a
    // holder is taken. A holder is never disturbed; a process taking the lease at that moment tries again.
    private static bool IsHeld(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read,
                OperatingSystem.IsWindows() ? FileShare.Read | FileShare.Delete : FileShare.ReadWrite, bufferSize: 1, FileOptions.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return true;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // Being deleted: its holder released it.
            return false;
        }
    }

    private static LeaseContent? TryReadContent(string path)
    {
        byte[] bytes;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.None);
                if (stream.Length > MaximumContentBytes) return null;
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        else if (!UnixFileContent.TryRead(path, MaximumContentBytes, out bytes))
        {
            return null;
        }

        try
        {
            return bytes.Length == 0 ? null : JsonSerializer.Deserialize(bytes, SessionFilesJsonContext.Default.LeaseContent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A sharing violation or a lock violation on Windows; outside Windows, the lock of another handle (EWOULDBLOCK, 11 on
    // Linux and 35 on macOS), which .NET reports with the error number as the result code.
    private static bool IsSharingViolation(IOException exception)
        => OperatingSystem.IsWindows()
            ? (exception.HResult & 0xFFFF) is 32 or 33
            : exception.HResult is 11 or 35;

    private static DateTimeOffset? ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // The content of a lease file, as the other processes read it.
    internal sealed class LeaseContent
    {
        [JsonPropertyName("ownerKind")]
        public string? OwnerKind { get; set; }

        [JsonPropertyName("pid")]
        public int? Pid { get; set; }

        [JsonPropertyName("processStartUtc")]
        public DateTimeOffset? ProcessStartUtc { get; set; }

        [JsonPropertyName("instanceId")]
        public string? InstanceId { get; set; }

        [JsonPropertyName("acquiredUtc")]
        public DateTimeOffset? AcquiredUtc { get; set; }

        public static LeaseContent From(SessionLeaseHolder holder) => new()
        {
            OwnerKind = holder.OwnerKind,
            Pid = holder.ProcessId,
            ProcessStartUtc = holder.ProcessStartUtc,
            InstanceId = holder.InstanceId,
            AcquiredUtc = holder.AcquiredUtc,
        };

        public SessionLeaseHolder? ToHolder(string sessionId)
            => string.IsNullOrWhiteSpace(OwnerKind) ? null : new SessionLeaseHolder(sessionId, OwnerKind, Pid, ProcessStartUtc, InstanceId, AcquiredUtc);
    }
}
