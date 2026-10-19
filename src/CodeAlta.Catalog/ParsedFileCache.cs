namespace CodeAlta.Catalog;

/// <summary>
/// What files parse to, kept so that a file read often is read and parsed again only when it changes. Each read
/// compares the length, the time of the last write and, outside Windows, the permissions of the file with those of the
/// text it parsed last; the text itself is read and compared while that time is too recent to tell two writes apart.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe, and owned by the one instance that reads the files: an owner forgets everything it keeps after each of
/// its writes (<see cref="Invalidate"/>), and sees the writes of the others by the stamp of the file. A value is shared
/// by every read of the same text: the owner never changes it and never hands it out where it could be changed.
/// </para>
/// <para>
/// A read that fails, and a parse that throws, are not kept: the next read reads and parses the file again, and fails
/// the same way. A text read again as it was gives the value it gave, whatever its stamp: the parse is a function of the
/// path and the text alone.
/// </para>
/// <para>
/// A stamp is trusted once the file was written <see cref="RacyWindow"/> before it was seen: a later write takes a later
/// time, even on the file systems whose clock counts in steps (two seconds on FAT). Until then the text is read and
/// compared at each read. What is not seen: a writer that puts back the length and the time of the text it replaces (a
/// copy that keeps the times, a clock set back onto the very time of the file), a network file system that answers with
/// the times it read before (attribute caching of NFS, for the writes of another computer), and, on Windows, a change of
/// the access rights alone. Such a change is seen with the next change of the file.
/// </para>
/// </remarks>
/// <typeparam name="T">The parsed form of a file.</typeparam>
internal sealed class ParsedFileCache<T> where T : class
{
    /// <summary>How long after its last write a file is read and compared at each read, whatever its stamp.</summary>
    internal static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(3);

    // Beyond that many files, what is kept is dropped before a file is added: a bound, not a policy.
    private const int MaximumEntries = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _generation;

    /// <summary>Initializes a cache that reads the time of the system.</summary>
    internal ParsedFileCache()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Initializes a cache with its clock.</summary>
    /// <param name="time">The clock that says how old a write is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is null.</exception>
    internal ParsedFileCache(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Reads a file and gives what its text parses to, reading and parsing it only when it changed.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="parse">Parses the text of the file: given the path and the text, it gives the value or throws.</param>
    /// <returns>The value of the text the file has now; shared with the other reads of the same text.</returns>
    /// <exception cref="IOException">The file cannot be read, as <see cref="File.ReadAllText(string)"/> says.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <remarks>Whatever <paramref name="parse"/> throws comes out as it is.</remarks>
    internal T Read(string path, Func<string, string, T> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        var observed = Observe(path);
        var (entry, generation) = Find(path);
        if (entry is { Settled: true } && observed?.Stamp == entry.Stamp) return entry.Value;
        return Keep(path, observed, entry, generation, File.ReadAllText(path), parse);
    }

    /// <summary>Reads a file as <see cref="Read"/> does, with an asynchronous read of its text.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="parse">Parses the text of the file: given the path and the text, it gives the value or throws.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value of the text the file has now; shared with the other reads of the same text.</returns>
    /// <exception cref="IOException">The file cannot be read, as <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> says.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    /// <remarks>Whatever <paramref name="parse"/> throws comes out as it is.</remarks>
    internal async Task<T> ReadAsync(string path, Func<string, string, T> parse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parse);
        var observed = Observe(path);
        var (entry, generation) = Find(path);
        if (entry is { Settled: true } && observed?.Stamp == entry.Stamp) return entry.Value;
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Keep(path, observed, entry, generation, content, parse);
    }

    /// <summary>Forgets every file: the owner wrote one, and what it kept may no longer be what the file says.</summary>
    /// <remarks>A read that started before is not kept either.</remarks>
    internal void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _entries.Clear();
        }
    }

    private (Entry? Entry, long Generation) Find(string path)
    {
        lock (_gate)
        {
            return (_entries.GetValueOrDefault(path), _generation);
        }
    }

    // The value of the text just read, kept with the stamp seen before the read when the file did not change meanwhile:
    // a stamp seen after the read could go with a text written after it.
    private T Keep(string path, Observation? observed, Entry? entry, long generation, string content, Func<string, string, T> parse)
    {
        var value = entry is not null && string.Equals(entry.Content, content, StringComparison.Ordinal)
            ? entry.Value
            : parse(path, content);
        if (observed is { } before && Observe(path)?.Stamp == before.Stamp)
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    if (_entries.Count >= MaximumEntries && !_entries.ContainsKey(path)) _entries.Clear();
                    _entries[path] = new Entry(before.Stamp, content, value, before.Settled);
                }
            }
        }

        return value;
    }

    // The stamp of the file, and whether its last write is old enough for the stamp to stand for its text from now on;
    // null when the file cannot be looked at, which is then read without being kept.
    private Observation? Observe(string path)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var stamp = new FileStamp(file.Length, file.LastWriteTimeUtc, OperatingSystem.IsWindows() ? 0 : (int)file.UnixFileMode);
            return new Observation(stamp, now - stamp.LastWriteTimeUtc > RacyWindow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc, int Mode);

    private readonly record struct Observation(FileStamp Stamp, bool Settled);

    // Settled: the file had been written RacyWindow before its stamp was seen, so the stamp alone stands for the text.
    private sealed record Entry(FileStamp Stamp, string Content, T Value, bool Settled);
}
