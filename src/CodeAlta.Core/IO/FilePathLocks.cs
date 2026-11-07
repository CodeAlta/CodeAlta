namespace CodeAlta.Core.IO;

/// <summary>
/// One lock per file within one process, for the read-modify-write stores: two owners of the same path wait for each
/// other, owners of different paths never. A path is taken whole (<see cref="Path.GetFullPath(string)"/>), without
/// regard to case on Windows and macOS. A lock that no owner holds or waits for is forgotten.
/// </summary>
/// <remarks>
/// <para>
/// It orders the threads of one process only: the processes of one profile still need a <see cref="LockFile"/>, and a
/// reader in another process still needs the replacement of <see cref="SafeFile"/>. Share one instance among the stores
/// that write the same files (an instance of a service, never static data).
/// </para>
/// <para>
/// Not reentrant. An owner never awaits it while it holds a lock of its thread (a <see langword="lock"/>, a mutex):
/// whoever takes both takes this one first.
/// </para>
/// </remarks>
public sealed class FilePathLocks
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Gets the number of paths whose lock is held or waited for.</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>Takes the lock of a path, blocking the thread while another owner holds it.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The held lock, released when it is disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    public IDisposable Acquire(string path)
    {
        var (key, entry) = Enter(path);
        try
        {
            entry.Gate.Wait();
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Held(this, key, entry);
    }

    /// <summary>Takes the lock of a path, waiting without blocking the thread while another owner holds it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The held lock, released when it is disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while it waited.</exception>
    public async ValueTask<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        var (key, entry) = Enter(path);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Held(this, key, entry);
    }

    // Counts the owner in before it waits, so that the entry stays while anyone holds or waits for it.
    private (string Key, Entry Entry) Enter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var key = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            entry.Owners++;
            return (key, entry);
        }
    }

    private void Leave(string key, Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Owners == 0) _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        // Every access to Owners is under the gate of the locks.
        public int Owners;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class Held(FilePathLocks owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            entry.Gate.Release();
            owner.Leave(key, entry);
        }
    }
}
