using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeAlta.Hosting;

/// <summary>Guards shared application state with conservative process-liveness inspection.</summary>
/// <remarks>
/// Unknown process inspection fails closed. PID/stale-file deletion and release races remain
/// unqualified; this does not establish cross-process or cross-head safety.
/// </remarks>
public sealed class CodeAltaSingleInstanceGuard : IDisposable
{
    private const string LockFileName = "alta.lock";
    private const int PidReadRetryCount = 20;
    private static readonly TimeSpan PidReadRetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly FileStream _lockStream;
    private bool _disposed;

    private CodeAltaSingleInstanceGuard(FileStream lockStream, string lockFilePath)
    {
        _lockStream = lockStream;
        LockFilePath = lockFilePath;
    }

    /// <summary>Gets the absolute path of the acquired lock file.</summary>
    public string LockFilePath { get; }

    /// <summary>Acquires the existing guard under the default user-profile .alta directory.</summary>
    /// <returns>The caller-owned guard.</returns>
    /// <exception cref="InvalidOperationException">The user profile directory is unavailable.</exception>
    /// <exception cref="CodeAltaAlreadyRunningException">The lock file could not be acquired.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock directory or file is denied.</exception>
    /// <exception cref="IOException">The directory or PID write fails.</exception>
    public static CodeAltaSingleInstanceGuard Acquire()
        => Acquire(GetDefaultLockFilePath());

    /// <summary>Acquires the default guard while publishing actual acquisition and rollback evidence.</summary>
    /// <param name="evidence">A fresh, caller-owned slot established before acquisition.</param>
    /// <returns>The acquired guard.</returns>
    /// <exception cref="ArgumentNullException">The evidence is null.</exception>
    /// <exception cref="InvalidOperationException">The evidence was already used or the profile is unavailable.</exception>
    /// <exception cref="Exception">Acquisition or initialization fails; failed rollback preserves both failures.</exception>
    public static CodeAltaSingleInstanceGuard Acquire(AcquisitionEvidence<FileStream> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Acquire(GetDefaultLockFilePath(), evidence);
    }

    /// <summary>Acquires the existing guard at an explicitly supplied lock-file path.</summary>
    /// <param name="lockFilePath">The lock-file path; automation must supply a task-owned path.</param>
    /// <returns>The caller-owned guard.</returns>
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    /// <exception cref="ArgumentException">The path is empty, whitespace or invalid.</exception>
    /// <exception cref="CodeAltaAlreadyRunningException">The lock file could not be acquired.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock directory or file is denied.</exception>
    /// <exception cref="IOException">The directory or PID write fails.</exception>
    public static CodeAltaSingleInstanceGuard Acquire(string lockFilePath)
        => Acquire(lockFilePath, new AcquisitionEvidence<FileStream>());

    /// <summary>Acquires the existing guard using a prepared candidate/rollback evidence slot.</summary>
    /// <param name="lockFilePath">The unchanged lock-file path.</param>
    /// <param name="evidence">A fresh caller-owned slot that retains the actual stream before PID writing.</param>
    /// <returns>The caller-owned guard.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
    /// <exception cref="InvalidOperationException">The evidence was already used.</exception>
    /// <exception cref="Exception">Acquisition or initialization fails; failed rollback preserves both failures.</exception>
    public static CodeAltaSingleInstanceGuard Acquire(string lockFilePath, AcquisitionEvidence<FileStream> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        ArgumentNullException.ThrowIfNull(evidence);

        var fullLockFilePath = Path.GetFullPath(lockFilePath);
        var directory = Path.GetDirectoryName(fullLockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        CodeAltaSingleInstanceGuard? guard = null;
        AcquireCandidate(evidence, () => CreateLockFile(fullLockFilePath),
            stream =>
            {
                WriteCurrentProcessId(stream);
                guard = new CodeAltaSingleInstanceGuard(stream, fullLockFilePath);
            },
            static stream => stream.Dispose());
        return guard!;
    }

    /// <summary>Retains one acquisition's actual candidate and distinct initialization/rollback outcomes.</summary>
    /// <typeparam name="T">The concrete reference-type acquisition.</typeparam>
    /// <remarks>A candidate remains available after successful rollback for identity auditing; it must not be disposed again.</remarks>
    public sealed class AcquisitionEvidence<T> where T : class
    {
        internal bool Used { get; set; }
        /// <summary>Gets the actual candidate, published before initialization begins.</summary>
        public T? Candidate { get; internal set; }
        /// <summary>Gets an acquisition failure before a candidate was returned.</summary>
        public Exception? AcquisitionFailure { get; internal set; }
        /// <summary>Gets the exact initialization failure.</summary>
        public Exception? InitializationFailure { get; internal set; }
        /// <summary>Gets whether initialization completed successfully.</summary>
        public bool InitializationCompleted { get; internal set; }
        /// <summary>Gets whether the sole rollback invocation began.</summary>
        public bool RollbackAttempted { get; internal set; }
        /// <summary>Gets whether the actual rollback completed successfully.</summary>
        public bool RollbackCompleted { get; internal set; }
        /// <summary>Gets the exact rollback failure, independently of initialization failure.</summary>
        public Exception? RollbackFailure { get; internal set; }
    }

    /// <summary>Runs the guard's acquisition/publication/initialization/stream-only rollback decision.</summary>
    /// <typeparam name="T">The concrete reference-type acquisition.</typeparam>
    /// <param name="evidence">Fresh prepared evidence, retained by the caller before invoking this method.</param>
    /// <param name="acquire">Returns the actual candidate; internally unreturned acquisitions remain its responsibility.</param>
    /// <param name="initialize">Initializes the already-published candidate.</param>
    /// <param name="rollback">Releases only that candidate after initialization failure; never retried.</param>
    /// <returns>The initialized candidate.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The evidence was already used or acquisition returned null.</exception>
    /// <exception cref="AggregateException">Both initialization and rollback fail, in that order without flattening.</exception>
    /// <exception cref="Exception">The sole acquisition or initialization failure is rethrown unchanged.</exception>
    public static T AcquireCandidate<T>(AcquisitionEvidence<T> evidence, Func<T> acquire,
        Action<T> initialize, Action<T> rollback) where T : class
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(rollback);
        if (evidence.Used) throw new InvalidOperationException("Acquisition evidence cannot be reused.");
        evidence.Used = true;
        try { evidence.Candidate = acquire() ?? throw new InvalidOperationException("Acquisition returned no candidate."); }
        catch (Exception failure) { evidence.AcquisitionFailure = failure; throw; }
        try
        {
            initialize(evidence.Candidate);
            evidence.InitializationCompleted = true;
            return evidence.Candidate;
        }
        catch (Exception primary)
        {
            evidence.InitializationFailure = primary;
            evidence.RollbackAttempted = true;
            try { rollback(evidence.Candidate); evidence.RollbackCompleted = true; }
            catch (Exception cleanup)
            {
                evidence.RollbackFailure = cleanup;
                throw new AggregateException(primary, cleanup);
            }
            throw;
        }
    }

    /// <summary>Releases this guard and attempts to remove its lock file; repeated calls are ignored.</summary>
    /// <exception cref="IOException">The underlying stream could not be disposed.</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lockStream.Dispose();
        TryDeleteLockFile(LockFilePath);
        _disposed = true;
    }

    internal static string GetDefaultLockFilePath()
        => Path.Combine(GetAltaHomeDirectory(), LockFileName);

    private static string GetAltaHomeDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException("Unable to determine the user profile directory for the CodeAlta lock file.");
        }

        return Path.Combine(userProfile, ".alta");
    }

    private static void WriteCurrentProcessId(FileStream lockStream)
    {
        lockStream.SetLength(0);
        var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var writer = new StreamWriter(lockStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        Exception? bodyFailure = null;
        try
        {
            writer.WriteLine(processId);
            writer.Flush();
            lockStream.Flush(flushToDisk: true);
            lockStream.Position = 0;
        }
        catch (Exception failure) { bodyFailure = failure; throw; }
        finally
        {
            try { writer.Dispose(); }
            catch (Exception cleanup) when (bodyFailure is not null) { throw new AggregateException(bodyFailure, cleanup); }
        }
    }

    private static FileStream CreateLockFile(string lockFilePath)
    {
        while (true)
        {
            try
            {
                return new FileStream(
                    lockFilePath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read);
            }
            catch (IOException ex)
            {
                var runningProcessId = ReadRunningProcessId(lockFilePath);
                if (runningProcessId is { } processId && !IsProcessRunning(processId) && TryDeleteLockFile(lockFilePath))
                {
                    continue;
                }

                throw new CodeAltaAlreadyRunningException(runningProcessId, ex);
            }
        }
    }

    private static bool IsProcessRunning(int processId)
        => IsProcessRunning<Process>(processId, Process.GetProcessById, static process => process.HasExited);

    /// <summary>Returns false only for lookup absence or observed exit followed by successful release.</summary>
    /// <remarks>
    /// For a positive PID, the .NET GetProcessById adapter reports a missing process via ArgumentException.
    /// Only that lookup-stage exception establishes absence; inspection and release errors fail closed.
    /// Invalid PIDs and null resources are unknown. Every acquired resource is released once, without retry.
    /// Observed process absence is not ownership evidence or authority over the current lock pathname.
    /// </remarks>
    internal static bool IsProcessRunning<TProcess>(
        int processId,
        Func<int, TProcess?> getProcessById,
        Func<TProcess, bool> hasExited)
        where TProcess : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(getProcessById);
        ArgumentNullException.ThrowIfNull(hasExited);

        if (processId <= 0)
        {
            return true;
        }

        TProcess? process;
        try
        {
            process = getProcessById(processId);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }

        if (process is null)
        {
            return true;
        }

        try
        {
            using (process)
            {
                return !hasExited(process);
            }
        }
        catch
        {
            // An inspection or release failure must never authorize stale-file reclamation.
            return true;
        }
    }

    private static bool TryDeleteLockFile(string lockFilePath)
    {
        try
        {
            File.Delete(lockFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int? ReadRunningProcessId(string lockFilePath)
    {
        for (var attempt = 0; attempt < PidReadRetryCount; attempt++)
        {
            if (TryReadRunningProcessId(lockFilePath, out var processId))
            {
                return processId;
            }

            Thread.Sleep(PidReadRetryDelay);
        }

        return null;
    }

    private static bool TryReadRunningProcessId(string lockFilePath, out int processId)
    {
        processId = 0;
        try
        {
            using var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd().Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out processId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Reports that the existing shared-state lock could not be acquired.</summary>
public sealed class CodeAltaAlreadyRunningException : Exception
{
    /// <summary>Initializes a lock-acquisition failure with the observed owner information.</summary>
    /// <param name="processId">The recorded process ID, or null when it could not be read.</param>
    /// <param name="innerException">The underlying acquisition failure.</param>
    public CodeAltaAlreadyRunningException(int? processId, Exception? innerException)
        : base(CreateMessage(processId), innerException)
    {
        ProcessId = processId;
    }

    /// <summary>Gets the recorded process ID, or null when it could not be read.</summary>
    public int? ProcessId { get; }

    private static string CreateMessage(int? processId)
    {
        var processText = processId is { } pid
            ? $" with PID {pid.ToString(CultureInfo.InvariantCulture)}"
            : " with an unknown PID";

        return $"An alta process is already running{processText}. CodeAlta allows only one application instance per machine because multiple instances would access the same sessions and shared application state.";
    }
}
