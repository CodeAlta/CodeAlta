namespace CodeAlta.Plugin.Git;

/// <summary>
/// What a hosting service answered about the things its filters offer (teams, sprints, types), kept for a while:
/// the Issues tab asks for them on every listing, and they change seldom. Callers that ask for the same key while it is
/// read wait for that one read.
/// </summary>
/// <remarks>Owned by one lookup; the keys name the credentials the answers were read with, so they are never given to another.</remarks>
internal sealed class GitHostValueCache
{
    private const int MaximumEntries = 256;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, object Value)> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<object?>> _reading = new(StringComparer.Ordinal);

    // What a read ends with when the caller that read was canceled: those that wait read again.
    private readonly object _abandoned = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _duration;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="duration">How long an answer is kept.</param>
    public GitHostValueCache(TimeProvider time, TimeSpan duration)
        => (_time, _duration) = (time, duration);

    /// <summary>
    /// Gets the kept answer of a key, or reads it; an answer that is null (a refusal) is not kept. While a key is read,
    /// the other callers of that key wait for the same read: its failure, a timeout included, is theirs too, but when
    /// the caller that reads is canceled, one of those that wait reads in its place.
    /// </summary>
    /// <typeparam name="T">The type of the answer.</typeparam>
    /// <param name="key">What names the answer: the credentials and the address it was read from.</param>
    /// <param name="read">Reads the answer, observing <paramref name="cancellationToken"/>; only called for the caller that reads.</param>
    /// <param name="cancellationToken">The token of this caller: canceled, it stops reading or waiting, and the others go on.</param>
    /// <returns>The answer, or null.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<T?> GetAsync<T>(string key, Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<object?>? owner = null;
            Task<object?> shared;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var entry) && entry.Expires > _time.GetUtcNow() && entry.Value is T kept) return kept;
                if (!_reading.TryGetValue(key, out shared!))
                {
                    owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _reading[key] = shared = owner.Task;
                }
            }

            if (owner is null)
            {
                var answer = await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
                // The caller that read it was canceled, not this one: it reads again.
                if (ReferenceEquals(answer, _abandoned)) continue;
                return answer as T;
            }

            try
            {
                var value = await read().ConfigureAwait(false);
                lock (_lock)
                {
                    _reading.Remove(key);
                    if (value is not null) Keep(key, value);
                }

                owner.SetResult(value);
                return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_lock) _reading.Remove(key);
                owner.SetResult(_abandoned);
                throw;
            }
            catch (Exception exception)
            {
                // A timeout is not this caller's cancellation: the others would only wait for it again.
                lock (_lock) _reading.Remove(key);
                owner.SetException(exception);
                // Nobody may be waiting: the failure is the one this caller throws.
                _ = owner.Task.Exception;
                throw;
            }
        }
    }

    private void Keep(string key, object value)
    {
        var now = _time.GetUtcNow();
        if (_entries.Count >= MaximumEntries)
        {
            foreach (var stale in _entries.Where(pair => pair.Value.Expires <= now).Select(static pair => pair.Key).ToArray()) _entries.Remove(stale);
            if (_entries.Count >= MaximumEntries) _entries.Clear();
        }

        _entries[key] = (now + _duration, value);
    }
}
