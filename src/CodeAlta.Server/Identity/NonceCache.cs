using System.Collections.Concurrent;

namespace CodeAlta.Server.Identity;

/// <summary>
/// The nonces of signed requests seen recently, per device, so that a captured request is refused when it comes again.
/// A nonce is kept as long as a request carrying it could still pass the clock check; older ones are forgotten.
/// </summary>
/// <remarks>The cache is bounded: when it is full of nonces still in their window, new requests are refused (the server is busy) rather than let in unchecked.</remarks>
public sealed class NonceCache
{
    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly int _capacity;
    private readonly Lock _sweepGate = new();
    private long _lastSweep;

    /// <summary>Initializes a new instance of the <see cref="NonceCache"/> class.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="retention">How long a nonce is kept: at least twice the accepted clock skew.</param>
    /// <param name="capacity">The most nonces kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> or <paramref name="capacity"/> is not positive.</exception>
    public NonceCache(TimeProvider time, TimeSpan retention, int capacity = 100_000)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _time = time;
        _retention = retention;
        _capacity = capacity;
    }

    /// <summary>Gets how many nonces are kept.</summary>
    public int Count => _seen.Count;

    /// <summary>Records a nonce of a device, unless it was seen within the retention.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="nonce">The nonce.</param>
    /// <returns><see cref="NonceResult.Fresh"/> for a new nonce, <see cref="NonceResult.Replayed"/> for one seen, <see cref="NonceResult.Full"/> when the cache is full.</returns>
    public NonceResult TryRecord(string deviceId, string nonce)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var key = string.Concat(deviceId, "\n", nonce);
        var expires = now + (long)_retention.TotalMilliseconds;
        if (_seen.Count >= _capacity) Sweep(now, force: true);
        else if (now - Volatile.Read(ref _lastSweep) > (long)_retention.TotalMilliseconds) Sweep(now, force: false);
        if (_seen.Count >= _capacity) return NonceResult.Full;

        while (true)
        {
            if (_seen.TryAdd(key, expires)) return NonceResult.Fresh;
            if (!_seen.TryGetValue(key, out var held)) continue;
            if (held > now) return NonceResult.Replayed;
            // Seen long ago: past its window, a request with this nonce fails the clock check anyway.
            if (_seen.TryUpdate(key, expires, held)) return NonceResult.Fresh;
        }
    }

    private void Sweep(long now, bool force)
    {
        lock (_sweepGate)
        {
            if (!force && now - _lastSweep <= (long)_retention.TotalMilliseconds) return;
            Volatile.Write(ref _lastSweep, now);
            foreach (var (key, expires) in _seen)
            {
                if (expires <= now) _seen.TryRemove(new KeyValuePair<string, long>(key, expires));
            }
        }
    }
}

/// <summary>The result of <see cref="NonceCache.TryRecord"/>.</summary>
public enum NonceResult
{
    /// <summary>A new nonce, now recorded.</summary>
    Fresh,

    /// <summary>A nonce seen within its window: a replay.</summary>
    Replayed,

    /// <summary>The cache is full: the request is refused for now.</summary>
    Full,
}
