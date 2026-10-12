using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

/// <summary>What a fork or a rewind of a session was made from.</summary>
/// <param name="Turns">The turns of the session before the operation.</param>
/// <param name="KeptTurnCount">The number of turns before the point: those of the fork, or those the rewound session keeps.</param>
public sealed record SessionViewBranchResult(IReadOnlyList<AgentSessionTurn> Turns, int KeptTurnCount)
{
    /// <summary>Gets the first turn after the point; null when the point is after the last turn.</summary>
    public AgentSessionTurn? NextTurn => KeptTurnCount < Turns.Count ? Turns[KeptTurnCount] : null;
}

public sealed partial class SessionViewJournalStore
{
    /// <summary>Creates the journal of a session that starts with the turns of another one up to a point.</summary>
    /// <param name="source">The session to fork.</param>
    /// <param name="target">
    /// The new session: its identifier, creation time, title, parent, folders and creator. Its lineage is set here.
    /// </param>
    /// <param name="beforeRunId">The run of the turn the fork stops before; null to keep every turn that is complete.</param>
    /// <param name="activeRunId">The run of the source that is in progress, which is never part of a fork; null when it is idle.</param>
    /// <param name="cancellationToken">Cancels before the journal of the new session exists.</param>
    /// <returns>The turns of the source and how many the new session starts with.</returns>
    /// <remarks>
    /// No provider is executed and the source is not written: it may run. The records of the source are copied under
    /// the identifier of the new session (see <c>AgentJournalBranchWriter</c>), then the summary, the state and the local
    /// state of the new session are written. The state is the one of the point, so that the next turn of the new session
    /// continues from there; it carries the choices of the source (provider, model, effort, prompt, permission mode) and
    /// none of its queued prompts. The new session is neither a child nor the parent of the source.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A session is null.</exception>
    /// <exception cref="ArgumentException">The new session has no identifier or creation time of its own.</exception>
    /// <exception cref="AgentSessionHistoryException">The source or the turn is missing, or the turn starts after the run in progress.</exception>
    /// <exception cref="InvalidOperationException">The source changed before the point while it was copied.</exception>
    /// <exception cref="InvalidDataException">A record of the source cannot be given to another session.</exception>
    /// <exception cref="IOException">Reading or writing a journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    public async Task<SessionViewBranchResult> ForkAsync(SessionViewDescriptor source, SessionViewDescriptor target, string? beforeRunId,
        string? activeRunId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var store = CreateSessionStore();
        var point = await store.ReadBranchPointAsync(source.SessionId, beforeRunId, activeRunId, cancellationToken).ConfigureAwait(false);
        await ForkAsync(store, source, target, point, AgentSessionForkKind.Fork, cancellationToken).ConfigureAwait(false);
        return new(point.Turns, point.KeptTurnCount);
    }

    /// <summary>Removes from a session the turn of a run and every turn after it.</summary>
    /// <param name="session">The session to rewind.</param>
    /// <param name="beforeRunId">The run of the first turn to remove.</param>
    /// <param name="removed">
    /// A new session that keeps the session as it was before the rewind, with every turn; null to discard what is removed.
    /// </param>
    /// <param name="cancellationToken">Cancels before the journal is replaced.</param>
    /// <returns>The turns of the session before the rewind and how many it keeps.</returns>
    /// <remarks>
    /// No provider is executed. The session keeps its identifier, its title and its local choices; its state becomes the
    /// one written before the removed turn, so that the next turn continues from there. The replacement of the journal
    /// is one rename (see <c>FileSystemAgentSessionStore.CommitRewindAsync</c>). Files that the removed turns changed in
    /// the working folder are not restored. The caller holds the idle-session mutation admission and has checked that
    /// nothing is queued.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="ArgumentException">The run is blank.</exception>
    /// <exception cref="AgentSessionHistoryException">The session or the turn is missing.</exception>
    /// <exception cref="InvalidOperationException">The session changed since its turns were read.</exception>
    /// <exception cref="IOException">Preparing or replacing a journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    public async Task<SessionViewBranchResult> RewindAsync(SessionViewDescriptor session, string beforeRunId, SessionViewDescriptor? removed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeRunId);
        var store = CreateSessionStore();
        var point = await store.ReadBranchPointAsync(session.SessionId, beforeRunId, activeRunId: null, cancellationToken).ConfigureAwait(false);
        if (removed is not null)
        {
            await ForkAsync(store, session, removed, point, AgentSessionForkKind.Rewind, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var summary = point.Summary with { Usage = point.State.Usage, RewindCount = point.Summary.RewindCount + 1, UpdatedAt = now };
            var state = BranchState(point, point.State, session.SessionId, AgentProviderBranchKind.Rewind, atEnd: false, now);
            var path = GetPath(session.SessionId, session.CreatedAt);
            var local = await ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false)
                ?? new SessionViewLocalState();
            // The count is of the messages that were there: it is counted again when the session is shown.
            local.MessageCount = null;
            await store.CommitRewindAsync(point, summary, state, [CreateStateEvent(session, local).ToJson()], cancellationToken).ConfigureAwait(false);
            InvalidateLatestStateCache(path);
            await _sessionCache.UpsertSessionViewStateAsync(session.SessionId, path, local, CancellationToken.None).ConfigureAwait(false);
        }
        catch when (removed is not null)
        {
            // The session is as it was: the copy that was to keep what the rewind removes has no reason to stay.
            try { await store.DeleteSessionAsync(removed.SessionId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            throw;
        }

        return new(point.Turns, point.KeptTurnCount);
    }

    private async Task ForkAsync(FileSystemAgentSessionStore store, SessionViewDescriptor source, SessionViewDescriptor target,
        AgentSessionBranchPoint point, AgentSessionForkKind kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.SessionId) || target.CreatedAt == default
            || string.Equals(target.SessionId, source.SessionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A fork needs an identifier and a creation time of its own.", nameof(target));
        var now = DateTimeOffset.UtcNow;
        // What a rewind removes was written before the session that keeps it exists: its own records start with the
        // first removed turn. Those of a fork start when it is created.
        var keepsRewind = kind == AgentSessionForkKind.Rewind;
        var origin = new AgentSessionForkOrigin
        {
            SessionId = source.SessionId,
            RootSessionId = point.Summary.ForkedFrom?.RootSessionId ?? source.SessionId,
            Title = string.IsNullOrWhiteSpace(source.Title) ? point.Summary.Title : source.Title,
            Kind = kind,
            TurnCount = point.KeptTurnCount,
            BeforeRunId = point.NextTurn?.RunId,
            ForkedAt = target.CreatedAt,
            CountedFrom = keepsRewind && point.NextTurn is { } next ? next.StartedAt : target.CreatedAt,
        };
        target.ForkedFrom = origin;
        var basis = keepsRewind ? point.CurrentState : point.State;
        var summary = point.Summary with
        {
            SessionId = target.SessionId,
            Title = string.IsNullOrWhiteSpace(target.Title) ? null : target.Title,
            WorkingDirectory = target.WorkingDirectory,
            WorktreeDirectory = string.IsNullOrWhiteSpace(target.WorktreeDirectory) ? null : target.WorktreeDirectory,
            ParentSessionId = string.IsNullOrWhiteSpace(target.ParentSessionId) ? null : target.ParentSessionId,
            CreatedBySessionId = string.IsNullOrWhiteSpace(target.CreatedBy?.SourceSessionId) ? null : target.CreatedBy.SourceSessionId,
            CreatedByRunId = null,
            ForkedFrom = origin,
            RewindCount = 0,
            CreatedAt = target.CreatedAt,
            UpdatedAt = now,
            Usage = basis.Usage,
        };
        // The session that keeps a rewind holds every turn: it continues from the end. A fork continues from its point,
        // which is the end of the conversation only when no turn follows it.
        var state = BranchState(point, basis, target.SessionId, AgentProviderBranchKind.Fork, atEnd: keepsRewind || point.NextTurn is null, now);
        var sourceLocal = await ReadLatestStateAsync(source.SessionId, source.CreatedAt, cancellationToken).ConfigureAwait(false);
        var local = new SessionViewLocalState
        {
            ProviderKey = sourceLocal?.ProviderKey,
            ModelId = sourceLocal?.ModelId,
            ReasoningEffort = sourceLocal?.ReasoningEffort,
            PermissionMode = sourceLocal?.PermissionMode,
            AgentPromptId = sourceLocal?.AgentPromptId,
            Archived = target.Status == SessionViewStatus.Archived,
            ParentSessionId = target.ParentSessionId,
            CreatedBy = target.CreatedBy,
        };
        await store.CommitForkAsync(point, summary, state, CreateHeaderEvent(target).ToJson(), [CreateStateEvent(target, local).ToJson()],
            includeRemainder: keepsRewind, cancellationToken).ConfigureAwait(false);
        var path = GetPath(target.SessionId, target.CreatedAt);
        await _sessionCache.UpsertSessionViewHeaderAsync(target.SessionId, path, SessionViewJournalHeader.FromDescriptor(target), CancellationToken.None)
            .ConfigureAwait(false);
        await _sessionCache.UpsertSessionViewStateAsync(target.SessionId, path, local, CancellationToken.None).ConfigureAwait(false);
    }

    // The state a session continues from: the one of the point, for the provider the session uses now. A state written
    // for another provider, before the session changed provider, says nothing the current one can take up.
    private static AgentSessionState BranchState(AgentSessionBranchPoint point, AgentSessionState basis, string sessionId,
        AgentProviderBranchKind kind, bool atEnd, DateTimeOffset now)
    {
        var state = basis with { SessionId = sessionId, UpdatedAt = now };
        var summary = point.Summary;
        return string.Equals(state.ProviderKey, summary.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(state.ProtocolFamily, summary.ProtocolFamily, StringComparison.OrdinalIgnoreCase)
                ? state with { ProviderBranch = new(kind, atEnd) }
                : state with
                {
                    ProviderKey = summary.ProviderKey,
                    ProtocolFamily = summary.ProtocolFamily,
                    ProviderSessionId = null,
                    ProviderState = null,
                    ProviderBranch = null,
                };
    }
}
