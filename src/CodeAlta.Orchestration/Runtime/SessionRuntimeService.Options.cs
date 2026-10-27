using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Thrown when a session that cannot start sessions of its own (<see cref="AgentSessionOptions.SubSessions"/> off) creates
/// or forks a session.
/// </summary>
public sealed class SubSessionsOffException : InvalidOperationException
{
    /// <summary>Initializes the exception for a session.</summary>
    /// <param name="sessionId">The session that asked.</param>
    public SubSessionsOffException(string sessionId)
        : base(SessionRuntimeService.SubSessionsOffMessage)
    {
        SessionId = sessionId;
    }

    /// <summary>Gets the session that asked.</summary>
    public string SessionId { get; }
}

public sealed partial class SessionRuntimeService
{
    /// <summary>What a session without sub-sessions is told when it tries to start one.</summary>
    public const string SubSessionsOffMessage =
        "This session was created without sub-sessions: it cannot create, fork or start sessions of its own. Do the work yourself with the tools of this session.";

    /// <summary>
    /// Gets the options of a session (<see cref="AgentSessionOptions"/>): those of its attachment while it is attached, else
    /// those saved with it. A session that is not known has the defaults.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<AgentSessionOptions> GetSessionOptionsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated) return entry.Options;
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return AgentSessionOptions.Of(session?.Options);
    }

    /// <summary>
    /// Saves the options of an owned session. The tools and the instructions of the session follow them from its next
    /// prompt: its attachment is replaced then, as when its tools change.
    /// </summary>
    /// <param name="session">The session, resolved by the owner.</param>
    /// <param name="options">The options; null for the defaults.</param>
    /// <returns>A task that completes once the options are saved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    internal Task SetOwnedOptionsAsync(SessionViewDescriptor session, AgentSessionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(session);
        var stored = AgentSessionOptions.Stored(options);
        return AdmitAsync(async () =>
        {
            using var lease = HoldForWrite(session.SessionId);
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
            await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
            {
                if (_entries.TryGetValue(session.SessionId, out var entry) && !entry.IsTerminated) entry.Options = AgentSessionOptions.Of(stored);
                var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                if (!Equals(AgentSessionOptions.Stored(localState?.Options), stored))
                {
                    if (localState is null)
                    {
                        localState = new SessionViewLocalState();
                        CopySessionMetadata(session, localState);
                    }

                    localState.Options = stored;
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                }

                session.Options = stored;
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            PublishSessionCatalogEvent(session);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Gets the options a session is created with when another session may create it: those asked, narrowed by the
    /// options of its creator (<see cref="AgentSessionOptions.Within"/>). A creator without sub-sessions creates none.
    /// </summary>
    /// <param name="asked">The options asked for the new session, or null for the defaults.</param>
    /// <param name="creatorSessionId">The session that creates it, or null when none does (the user, a host).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The options to record: null for the defaults.</returns>
    /// <exception cref="SubSessionsOffException">The creator cannot start sessions of its own.</exception>
    public async Task<AgentSessionOptions?> GetCreatedSessionOptionsAsync(AgentSessionOptions? asked, string? creatorSessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(creatorSessionId)) return AgentSessionOptions.Stored(asked);
        var creator = await GetSessionOptionsAsync(creatorSessionId, cancellationToken).ConfigureAwait(false);
        if (!creator.SubSessions) throw new SubSessionsOffException(creatorSessionId);
        return AgentSessionOptions.Stored(AgentSessionOptions.Of(asked).Within(creator));
    }
}
