using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeAlta.Hosting;

/// <summary>Acquires the existing alta.lock admission guard for shared application state.</summary>
/// <remarks>
/// This extraction preserves the existing PID/stale-file algorithm, including its unqualified
/// process-inspection and deletion races. It does not establish cross-process or cross-head safety.
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

    /// <summary>Acquires the existing guard at an explicitly supplied lock-file path.</summary>
    /// <param name="lockFilePath">The lock-file path; automation must supply a task-owned path.</param>
    /// <returns>The caller-owned guard.</returns>
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    /// <exception cref="ArgumentException">The path is empty, whitespace or invalid.</exception>
    /// <exception cref="CodeAltaAlreadyRunningException">The lock file could not be acquired.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock directory or file is denied.</exception>
    /// <exception cref="IOException">The directory or PID write fails.</exception>
    public static CodeAltaSingleInstanceGuard Acquire(string lockFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);

        var fullLockFilePath = Path.GetFullPath(lockFilePath);
        var directory = Path.GetDirectoryName(fullLockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockStream = CreateLockFile(fullLockFilePath);

        try
        {
            WriteCurrentProcessId(lockStream);
            return new CodeAltaSingleInstanceGuard(lockStream, fullLockFilePath);
        }
        catch
        {
            lockStream.Dispose();
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
        using var writer = new StreamWriter(lockStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.WriteLine(processId);
        writer.Flush();
        lockStream.Flush(flushToDisk: true);
        lockStream.Position = 0;
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
        => IsProcessRunning(processId, Process.GetProcessById, static process => process.HasExited);

    internal static bool IsProcessRunning(
        int processId,
        Func<int, Process> getProcessById,
        Func<Process, bool> hasExited)
    {
        ArgumentNullException.ThrowIfNull(getProcessById);
        ArgumentNullException.ThrowIfNull(hasExited);

        try
        {
            using var process = getProcessById(processId);
            return !hasExited(process);
        }
        catch
        {
            // Process.HasExited can throw (for example, access denied while opening the process).
            return false;
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
