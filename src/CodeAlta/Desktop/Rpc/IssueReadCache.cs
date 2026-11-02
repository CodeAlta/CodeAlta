namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// What the Issues tab read from a tracker, kept a while: a list or an item asked again within <see cref="Duration"/> is
/// answered without a request, with the time it was read, which the tab shows beside a refresh that reads it again.
/// </summary>
/// <remarks>Owned by one <see cref="IssuesService"/>; the oldest entries leave first when the cache is full.</remarks>
/// <typeparam name="T">What is kept.</typeparam>
internal sealed class IssueReadCache<T> where T : class
{
    /// <summary>How long what was read is kept.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset ReadAt, T Value)> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _capacity;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="capacity">The most entries kept.</param>
    public IssueReadCache(TimeProvider time, int capacity)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        (_time, _capacity) = (time, capacity);
    }

    /// <summary>Gets the time of now, as the cache counts it.</summary>
    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Gets what is kept for a key and when it was read; false when nothing is, or it is older than <see cref="Duration"/>.</summary>
    public bool TryGet(string key, out T value, out DateTimeOffset readAt)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && Now - entry.ReadAt < Duration)
            {
                (value, readAt) = (entry.Value, entry.ReadAt);
                return true;
            }

            _entries.Remove(key);
        }

        (value, readAt) = (null!, default);
        return false;
    }

    /// <summary>Keeps what was read for a key, at the time given; the oldest entry leaves when the cache is full.</summary>
    public void Set(string key, T value, DateTimeOffset readAt)
    {
        lock (_gate)
        {
            _entries[key] = (readAt, value);
            if (_entries.Count <= _capacity) return;
            var now = Now;
            foreach (var stale in _entries.Where(pair => now - pair.Value.ReadAt >= Duration).Select(static pair => pair.Key).ToArray()) _entries.Remove(stale);
            while (_entries.Count > _capacity) _entries.Remove(_entries.MinBy(static pair => pair.Value.ReadAt).Key);
        }
    }
}
