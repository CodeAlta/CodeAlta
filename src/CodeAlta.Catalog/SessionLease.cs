namespace CodeAlta.Catalog;

/// <summary>
/// The lease of a session this process holds (<see cref="SessionLeaseStore.TryAcquire"/>): while it is not disposed, no
/// other process, and no other lease of this process, can take the session. The operating system releases it when the
/// process ends, so a crash leaves nothing to clean up.
/// </summary>
public sealed class SessionLease : IDisposable
{
    private FileStream? _stream;

    internal SessionLease(string path, FileStream stream, SessionLeaseHolder holder)
    {
        Path = path;
        _stream = stream;
        Holder = holder;
    }

    /// <summary>Gets the session.</summary>
    public string SessionId => Holder.SessionId;

    /// <summary>Gets the path of the lease file.</summary>
    public string Path { get; }

    /// <summary>Gets what the lease says of its holder.</summary>
    public SessionLeaseHolder Holder { get; }

    /// <summary>Gets whether the lease was released.</summary>
    public bool IsReleased => Volatile.Read(ref _stream) is null;

    /// <summary>Releases the lease and deletes its file, best effort. Repeated calls do nothing.</summary>
    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null) return;
        if (OperatingSystem.IsWindows())
        {
            // Nobody may delete the file while the handle is open, this process included: the handle shares no deletion.
            // A process that opened it meanwhile keeps it, which is harmless.
            stream.Dispose();
            TryDelete(Path);
        }
        else
        {
            // The name goes while the lock is held: a process can then only open the file of the next holder, never the
            // one being released, whose lock it would take on a file that is no longer named.
            TryDelete(Path);
            stream.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next holder, which opens the file as it is.
        }
    }
}
