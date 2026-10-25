using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The leases of the sessions a runtime holds (<see cref="SessionLeaseStore"/>), and why it holds each: the session has an
/// attachment, or something holds it (a frontend shows it, a headless run opened it, a change is being written). The lease is
/// taken with the first claim and released with the last, so that another process can run the session once this one no
/// longer runs or shows it.
/// </summary>
/// <remarks>
/// A claim on a session another process holds is refused with <see cref="SessionInUseException"/>, and a claim whose lease
/// cannot be taken (the folder cannot be written) with <see cref="SessionLeaseUnavailableException"/>. Claims are counted in
/// memory under one lock; the lease itself is an open handle the operating system releases when the process ends.
/// </remarks>
internal sealed class SessionLeaseRegistry : IDisposable
{
    private readonly SessionLeaseStore _store;
    private readonly SessionLeaseOwner _owner;
    private readonly object _gate = new();
    private readonly Dictionary<string, Claims> _claims = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal SessionLeaseRegistry(SessionLeaseStore store, SessionLeaseOwner owner)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(owner);
        _store = store;
        _owner = owner;
    }

    internal SessionLeaseStore Store => _store;

    internal SessionLeaseOwner Owner => _owner;

    /// <summary>Gets the sessions whose lease this runtime holds now.</summary>
    internal IReadOnlyCollection<string> Held
    {
        get { lock (_gate) return [.. _claims.Keys]; }
    }

    /// <summary>Gets whether this runtime holds the lease of a session.</summary>
    internal bool Holds(string sessionId)
    {
        lock (_gate) return _claims.ContainsKey(sessionId);
    }

    /// <summary>
    /// Gets who holds the lease of a session when it is another process, or another lease of this one; null when this runtime
    /// holds it or nobody does.
    /// </summary>
    internal SessionLeaseHolder? HeldElsewhere(string sessionId)
    {
        if (!SessionLeaseStore.IsValidSessionId(sessionId)) return null;
        lock (_gate) if (_claims.ContainsKey(sessionId)) return null;
        try
        {
            return _store.TryReadHolder(sessionId);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Claims a session for its attachment; repeated claims count once.</summary>
    /// <exception cref="SessionInUseException">Another process holds the session.</exception>
    /// <exception cref="SessionLeaseUnavailableException">The lease cannot be taken.</exception>
    internal void ClaimEntry(string sessionId)
    {
        lock (_gate)
        {
            var claims = AcquireLocked(sessionId);
            if (claims is not null) claims.Entry = true;
        }
    }

    /// <summary>Drops the claim of the attachment of a session; the lease goes with the last claim.</summary>
    internal void ReleaseEntry(string sessionId)
    {
        lock (_gate)
        {
            if (!_claims.TryGetValue(sessionId, out var claims) || !claims.Entry) return;
            claims.Entry = false;
            ReleaseIfUnclaimedLocked(sessionId, claims);
        }
    }

    /// <summary>Holds a session until the returned handle is disposed.</summary>
    /// <returns>The handle; disposing it more than once releases once.</returns>
    /// <exception cref="SessionInUseException">Another process holds the session.</exception>
    /// <exception cref="SessionLeaseUnavailableException">The lease cannot be taken.</exception>
    internal IDisposable Hold(string sessionId)
    {
        lock (_gate)
        {
            var claims = AcquireLocked(sessionId);
            if (claims is null) return NoHold.Instance;
            claims.Holds++;
        }

        return new HoldHandle(this, sessionId);
    }

    /// <summary>Releases every lease, when the runtime closes.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var claims in _claims.Values) claims.Lease.Dispose();
            _claims.Clear();
        }
    }

    // Null when the session id cannot have a lease (no journal can be named after it either) or the runtime closed.
    private Claims? AcquireLocked(string sessionId)
    {
        if (_disposed || !SessionLeaseStore.IsValidSessionId(sessionId)) return null;
        if (_claims.TryGetValue(sessionId, out var claims)) return claims;
        SessionLease? lease;
        try
        {
            lease = _store.TryAcquire(sessionId, _owner);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fails closed: a lease that cannot be taken would let another process write the session too, while its journal
            // stays writable here.
            throw new SessionLeaseUnavailableException(sessionId, exception);
        }

        if (lease is null)
        {
            var holder = SafeReadHolder(sessionId);
            throw new SessionInUseException(sessionId, holder?.OwnerKind, holder?.ProcessId);
        }

        claims = new Claims(lease);
        _claims[sessionId] = claims;
        return claims;
    }

    private SessionLeaseHolder? SafeReadHolder(string sessionId)
    {
        try
        {
            return _store.TryReadHolder(sessionId);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void ReleaseIfUnclaimedLocked(string sessionId, Claims claims)
    {
        if (claims.Entry || claims.Holds > 0) return;
        _claims.Remove(sessionId);
        claims.Lease.Dispose();
    }

    private void Unhold(string sessionId)
    {
        lock (_gate)
        {
            if (!_claims.TryGetValue(sessionId, out var claims) || claims.Holds == 0) return;
            claims.Holds--;
            ReleaseIfUnclaimedLocked(sessionId, claims);
        }
    }

    private sealed class Claims(SessionLease lease)
    {
        public SessionLease Lease { get; } = lease;

        public bool Entry { get; set; }

        public int Holds { get; set; }
    }

    private sealed class HoldHandle(SessionLeaseRegistry registry, string sessionId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) registry.Unhold(sessionId);
        }
    }

    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose()
        {
        }
    }
}
