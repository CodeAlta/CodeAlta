using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Reads the turns of a session: the points it can be forked at or rewound to.</summary>
    /// <param name="sessionId">Exact session identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The turns, or null when the session does not exist or its journal cannot be read as turns.</returns>
    /// <exception cref="ArgumentException">The identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">The owner is closing.</exception>
    public Task<SessionTurnsSnapshot?> ReadSessionTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        return _runtime.ReadSessionTurnsAsync(sessionId, cancellationToken);
    }

    /// <summary>Forks an exact owned catalog session at a turn, or after its last complete one.</summary>
    /// <param name="sessionId">Exact session identity.</param>
    /// <param name="projectId">Exact project identity, or null for a global session.</param>
    /// <param name="workspacePath">Exact catalog workspace path.</param>
    /// <param name="beforeRunId">The run of the turn the fork stops before; null to keep every turn that is complete.</param>
    /// <param name="title">The title of the fork; null for the title of the session followed by "(fork)".</param>
    /// <returns>The fork, or why it was not created; <c>session_missing</c> when the session is not of the requested scope.</returns>
    /// <remarks>The session may run. No provider is started: the fork is a stored session, attached when it is first used.</remarks>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">The owner is closing.</exception>
    public async Task<SessionBranchOutcome> ForkSessionAsync(string sessionId, string? projectId, string workspacePath, string? beforeRunId, string? title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        if (!await IsOfScopeAsync(sessionId, projectId, workspacePath).ConfigureAwait(false)) return new("session_missing");
        return await _runtime.ForkSessionAsync(new SessionForkRequest { SessionId = sessionId, BeforeRunId = beforeRunId, Title = title }).ConfigureAwait(false);
    }

    /// <summary>Rewinds an exact owned catalog session to before a turn.</summary>
    /// <param name="sessionId">Exact session identity.</param>
    /// <param name="projectId">Exact project identity, or null for a global session.</param>
    /// <param name="workspacePath">Exact catalog workspace path.</param>
    /// <param name="beforeRunId">The run of the first turn to remove.</param>
    /// <param name="keepRemovedTurns">Whether the session as it was before the rewind is kept as a session of its own.</param>
    /// <returns>The session as it is afterwards, or why it was not rewound; <c>session_missing</c> when it is not of the requested scope.</returns>
    /// <remarks>The session must be idle. Files that the removed turns changed are not restored.</remarks>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">The owner is closing.</exception>
    public async Task<SessionBranchOutcome> RewindSessionAsync(string sessionId, string? projectId, string workspacePath, string beforeRunId, bool keepRemovedTurns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeRunId);
        lock (_gate)
        {
            if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService));
            // A send, a queue or another command of this owner that is under way for the session would be cut with it.
            if (_deleteWork is not null || _active.ContainsKey(sessionId) || _steering.Contains(sessionId) || _compacting.Contains(sessionId)
                || _abortingRuns.Contains(sessionId) || _queueing.Contains(sessionId) || _remoteControlling.ContainsKey(sessionId)
                || _operations.Values.Any(operation => !operation.Released && string.Equals(operation.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                || _queues.Values.Any(operation => !operation.Released && string.Equals(operation.Request.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                return new("session_in_use");
        }

        if (!await IsOfScopeAsync(sessionId, projectId, workspacePath).ConfigureAwait(false)) return new("session_missing");
        return await _runtime.RewindSessionAsync(new SessionRewindRequest
        {
            SessionId = sessionId,
            BeforeRunId = beforeRunId,
            KeepRemovedTurns = keepRemovedTurns,
        }).ConfigureAwait(false);
    }

    // The session the caller names is the one of the project and folder it shows: an identity alone is not a scope.
    private async Task<bool> IsOfScopeAsync(string sessionId, string? projectId, string workspacePath)
    {
        var session = await _runtime.ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        return session is not null && session.SessionId == sessionId && session.ProjectRef == projectId && session.WorkingDirectory == workspacePath
            && session.Kind == (projectId is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession);
    }
}
