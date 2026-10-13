namespace CodeAlta.Agent.Runtime;

/// <summary>Why a session starts with the turns of another one.</summary>
public enum AgentSessionForkKind
{
    /// <summary>The session was forked: it continues from a point of another session, which goes on as it was.</summary>
    Fork,

    /// <summary>The session holds what a rewind removed from another session: all its turns as they were before the rewind.</summary>
    Rewind,
}

/// <summary>
/// Where a session that starts with the turns of another one comes from. This is lineage only: the two sessions share
/// no journal, no run and no provider conversation, and neither is the parent of the other.
/// </summary>
public sealed record AgentSessionForkOrigin
{
    /// <summary>Gets the session the turns come from.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the session at the start of the chain of forks: the one whose identifier the requests of every session of
    /// the chain give a provider as the key of its prompt cache, so that a fork reads what its source wrote there.
    /// </summary>
    public required string RootSessionId { get; init; }

    /// <summary>Gets the title of the source when the session was created; the source may be renamed or deleted since.</summary>
    public string? Title { get; init; }

    /// <summary>Gets why the session starts with the turns of the source.</summary>
    public AgentSessionForkKind Kind { get; init; }

    /// <summary>Gets the number of turns that precede the point the session was created at.</summary>
    public int TurnCount { get; init; }

    /// <summary>Gets the run of the turn the point precedes; null when the point is after the last turn of the source.</summary>
    public string? BeforeRunId { get; init; }

    /// <summary>Gets when the session was created from the source.</summary>
    public required DateTimeOffset ForkedAt { get; init; }

    /// <summary>
    /// Gets the time from which the records of the session are its own: the older ones are those of the source, which
    /// are counted there. It is <see cref="ForkedAt"/> for a fork, and the start of the first removed turn for a session
    /// that holds what a rewind removed.
    /// </summary>
    public required DateTimeOffset CountedFrom { get; init; }
}
