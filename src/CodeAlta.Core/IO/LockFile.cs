using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace CodeAlta.Core.IO;

/// <summary>
/// A lock shared by the processes of one profile (the desktop, the TUI, <c>altacli</c>, a developer instance): a file
/// opened exclusively, held while the returned stream is open. Its owner reads, changes and writes what it guards, and
/// disposes the stream; a process that ends releases it with its handles.
/// </summary>
/// <remarks>
/// The lock file stays once released: deleting it on close would let a waiter open a file being deleted. Within one
/// process two owners exclude each other too, but a <see cref="FilePathLocks"/> before it spares the polling.
/// </remarks>
public static class LockFile
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Takes a lock file, waiting while another owner holds it.</summary>
    /// <param name="path">The lock file, created when it does not exist; its folder must exist.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <returns>The lock file, held until it is disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="TimeoutException">Another owner held it beyond <paramref name="timeout"/>.</exception>
    /// <exception cref="IOException">The lock file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock file is denied.</exception>
    public static FileStream Acquire(string path, TimeSpan timeout) => Acquire(path, timeout, CancellationToken.None);

    /// <summary>Takes a lock file, waiting while another owner holds it, until a time or a cancellation.</summary>
    /// <param name="path">The lock file, created when it does not exist; its folder must exist.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The lock file, held until it is disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="TimeoutException">Another owner held it beyond <paramref name="timeout"/>.</exception>
    /// <exception cref="IOException">The lock file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while it waited.</exception>
    public static FileStream Acquire(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquire(path, out var held)) return held;
            if (Expired(started, timeout)) throw TimedOut(path);
            if (cancellationToken.WaitHandle.WaitOne(RetryDelay)) cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Takes a lock file, waiting without blocking the thread while another owner holds it, until a cancellation.</summary>
    /// <param name="path">The lock file, created when it does not exist; its folder must exist.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The lock file, held until it is disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The lock file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while it waited.</exception>
    public static ValueTask<FileStream> AcquireAsync(string path, CancellationToken cancellationToken)
        => AcquireAsync(path, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>Takes a lock file, waiting without blocking the thread while another owner holds it, until a time or a cancellation.</summary>
    /// <param name="path">The lock file, created when it does not exist; its folder must exist.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The lock file, held until it is disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="TimeoutException">Another owner held it beyond <paramref name="timeout"/>.</exception>
    /// <exception cref="IOException">The lock file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while it waited.</exception>
    public static async ValueTask<FileStream> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquire(path, out var held)) return held;
            if (Expired(started, timeout)) throw TimedOut(path);
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Takes a lock file when no other owner holds it.</summary>
    /// <param name="path">The lock file, created when it does not exist; its folder must exist.</param>
    /// <param name="held">The lock file, held until it is disposed; null when another owner holds it.</param>
    /// <returns>Whether the lock was taken.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The lock file cannot be opened for another reason than another owner.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock file is denied.</exception>
    public static bool TryAcquire(string path, [NotNullWhen(true)] out FileStream? held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            return true;
        }
        catch (IOException exception) when (IsHeldByAnother(exception))
        {
            held = null;
            return false;
        }
    }

    /// <summary>
    /// Gets whether an open failed because another handle holds the file: a sharing (32) or lock (33) violation on Windows;
    /// elsewhere the lock of another handle (EWOULDBLOCK, 11 on Linux and 35 on macOS), which .NET reports with the error
    /// number as the result code.
    /// </summary>
    /// <param name="exception">The failure of the open.</param>
    /// <returns>Whether another handle holds the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static bool IsHeldByAnother(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return OperatingSystem.IsWindows()
            ? (exception.HResult & 0xFFFF) is 32 or 33
            : exception.HResult is 11 or 35;
    }

    private static bool Expired(long started, TimeSpan timeout)
        => timeout != Timeout.InfiniteTimeSpan && Stopwatch.GetElapsedTime(started) >= timeout;

    private static TimeoutException TimedOut(string path) => new($"The lock file '{path}' stayed held by another owner.");
}