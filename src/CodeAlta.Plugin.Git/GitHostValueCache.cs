namespace CodeAlta.Plugin.Git;

/// <summary>
/// What a hosting service answered about the things its filters offer (teams, sprints, types), kept for a while:
/// the Issues tab asks for them on every listing, and they change seldom.
/// </summary>
/// <remarks>Owned by one lookup, so the answers read with one set of credentials are never given to another.</remarks>
internal sealed class GitHostValueCache
{
    private const int MaximumEntries = 256;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, object Value)> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _duration;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="duration">How long an answer is kept.</param>
    public GitHostValueCache(TimeProvider time, TimeSpan duration)
        => (_time, _duration) = (time, duration);

    /// <summary>Gets the kept answer of a key, or reads it; an answer that is null (a refusal) is not kept.</summary>
    /// <typeparam name="T">The type of the answer.</typeparam>
    /// <param name="key">What names the answer: the address it was read from.</param>
    /// <param name="read">Reads the answer.</param>
    /// <returns>The answer, or null.</returns>
    public async Task<T?> GetAsync<T>(string key, Func<Task<T?>> read) where T : class
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Expires > now && entry.Value is T kept) return kept;
        }

        var value = await read().ConfigureAwait(false);
        if (value is null) return null;
        lock (_lock)
        {
            if (_entries.Count >= MaximumEntries)
            {
                foreach (var stale in _entries.Where(pair => pair.Value.Expires <= now).Select(static pair => pair.Key).ToArray()) _entries.Remove(stale);
                if (_entries.Count >= MaximumEntries) _entries.Clear();
            }

            _entries[key] = (now + _duration, value);
        }

        return value;
    }
}
