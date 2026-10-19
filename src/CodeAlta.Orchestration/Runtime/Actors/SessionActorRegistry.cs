using System.Collections.Concurrent;

namespace CodeAlta.Orchestration.Runtime.Actors;

internal sealed class SessionActorRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionActor> _actors = new(StringComparer.OrdinalIgnoreCase);
    // An actor starts its mailbox when it is made: two callers that race to make the actor of a session must not both
    // make one, or the one that is not kept runs forever.
    private readonly object _createGate = new();
    private readonly int _mailboxCapacity;
    private readonly Func<Exception, SessionActorSupervisorDecision>? _superviseException;
    private bool _disposed;

    public SessionActorRegistry(
        int mailboxCapacity = 128,
        Func<Exception, SessionActorSupervisorDecision>? superviseException = null)
    {
        if (mailboxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mailboxCapacity), mailboxCapacity, "Mailbox capacity must be greater than zero.");
        }

        _mailboxCapacity = mailboxCapacity;
        _superviseException = superviseException;
    }

    public int Count => _actors.Count;

    public SessionActor GetOrCreate(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfDisposed();
        if (_actors.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        lock (_createGate)
        {
            if (_actors.TryGetValue(sessionId, out existing))
            {
                return existing;
            }

            ThrowIfDisposed();
            var actor = new SessionActor(sessionId, _mailboxCapacity, _superviseException);
            _actors[sessionId] = actor;
            return actor;
        }
    }

    public bool TryGet(string sessionId, out SessionActor actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfDisposed();
        return _actors.TryGetValue(sessionId, out actor!);
    }

    public async ValueTask<bool> RemoveAsync(string sessionId, bool cancelPending = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfDisposed();
        if (!_actors.TryRemove(sessionId, out var actor))
        {
            return false;
        }

        await actor.StopAsync(cancelPending).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var actors = _actors.Values.ToArray();
        _actors.Clear();
        foreach (var actor in actors)
        {
            await actor.StopAsync(cancelPending: true).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SessionActorRegistry));
        }
    }
}
