namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The waits of the sessions that attach for the user's answer to the secrets that ask before use: stopping a session ends its wait
/// at once (<see cref="Stop"/>), and closing the runtime ends them all (<see cref="Close"/>), instead of letting them run their time.
/// </summary>
internal sealed class SessionSecretWaits
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<CancellationTokenSource>> _waits = new(StringComparer.Ordinal);
    private bool _closed;

    /// <summary>Starts the wait of a session that attaches; disposing the result ends it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The wait, whose token is canceled when the session is stopped or the runtime closes.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public Wait Begin(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // Never disposed: a stop that cancels it after the wait ended finds nothing to dispose of, and it has no timer.
        var source = new CancellationTokenSource();
        lock (_gate)
        {
            if (_closed)
            {
                source.Cancel();
            }
            else
            {
                if (!_waits.TryGetValue(sessionId, out var waits)) _waits[sessionId] = waits = [];
                waits.Add(source);
            }
        }

        return new Wait(this, sessionId, source);
    }

    /// <summary>Ends the waits of a session: the user stopped it.</summary>
    /// <param name="sessionId">The session.</param>
    public void Stop(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        List<CancellationTokenSource>? waits;
        lock (_gate) _waits.Remove(sessionId, out waits);
        foreach (var source in waits ?? []) source.Cancel();
    }

    /// <summary>Ends every wait, and the ones that start later at once: the runtime closes.</summary>
    public void Close()
    {
        CancellationTokenSource[] waits;
        lock (_gate)
        {
            _closed = true;
            waits = [.. _waits.Values.SelectMany(static list => list)];
            _waits.Clear();
        }

        foreach (var source in waits) source.Cancel();
    }

    private void End(string sessionId, CancellationTokenSource source)
    {
        lock (_gate)
        {
            if (!_waits.TryGetValue(sessionId, out var waits)) return;
            waits.Remove(source);
            if (waits.Count == 0) _waits.Remove(sessionId);
        }
    }

    /// <summary>The wait of one attachment.</summary>
    internal sealed class Wait : IDisposable
    {
        private readonly SessionSecretWaits _owner;
        private readonly string _sessionId;
        private readonly CancellationTokenSource _source;

        internal Wait(SessionSecretWaits owner, string sessionId, CancellationTokenSource source)
        {
            _owner = owner;
            _sessionId = sessionId;
            _source = source;
        }

        /// <summary>Gets the token canceled when the session is stopped or the runtime closes.</summary>
        public CancellationToken Token => _source.Token;

        /// <inheritdoc />
        public void Dispose() => _owner.End(_sessionId, _source);
    }
}
