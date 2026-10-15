using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Catalog;

/// <summary>
/// Serializes the writers of one configuration file: the stores of this process, which the desktop calls side by side,
/// and those of the user's other CodeAlta processes. A writer reads the file, changes its part and writes it back while
/// it holds the lock, so it never writes over a change made meanwhile or reads a file that is being written.
/// </summary>
/// <remarks>
/// The lock is a named system mutex derived from the full path of the file: it is held by a thread and is reentrant on
/// it, so it is taken and released by synchronous code only.
/// </remarks>
internal static class ConfigFileLock
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Takes the lock of a file, waiting while another writer holds it. Disposing the result releases it, on the same thread.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The held lock.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="IOException">Another writer has held the lock for too long.</exception>
    internal static IDisposable Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var mutex = new Mutex(false, Name(path));
        try
        {
            if (!mutex.WaitOne(Wait)) throw new IOException($"The configuration file '{path}' is being written by another writer.");
        }
        catch (AbandonedMutexException)
        {
            // Its writer ended without releasing it: the lock is held now all the same.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new Held(mutex);
    }

    // The same file gives the same name in every process; a name holds no separator, which a system name may not.
    internal static string Name(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) full = full.ToUpperInvariant();
        return "CodeAlta.config." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)), 0, 16);
    }

    private sealed class Held(Mutex mutex) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
