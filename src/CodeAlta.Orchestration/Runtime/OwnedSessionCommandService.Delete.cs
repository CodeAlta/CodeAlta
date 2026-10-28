namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Deletes an exact confirmed project and its journals, never the project's working files.</summary>
    /// <remarks>Reserves command admission until completion. A session of the project that runs, or retained commands, refuse deletion.
    /// A failure after removal starts can leave some sessions deleted; it must not be retried automatically.</remarks>
    /// <param name="projectId">Exact catalog identity.</param>
    /// <param name="projectPath">Exact catalog folder.</param>
    /// <param name="confirmedName">Project display name shown in the confirmation.</param>
    /// <param name="expectedArchived">Archive state shown in the confirmation.</param>
    /// <returns>ok, closed, busy, scope_missing, has_children, session_in_use or delete_unconfirmed.</returns>
    /// <exception cref="ArgumentException">A required identity or confirmation is blank.</exception>
    public Task<string> DeleteCatalogProjectAsync(string projectId, string projectPath, string confirmedName, bool expectedArchived)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedName);
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult("closed");
            // The reservation holds every session while the project is deleted. Work that already goes on refuses the
            // deletion only when it is of a session of the project, which the runtime asks about once it has read
            // them from disk: the sessions of the other projects go on.
            if (_deleteWork is not null) return Task.FromResult("busy");
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = completion.Task;
            _ = DeleteCatalogProjectOwnedAsync(projectId, projectPath, confirmedName, expectedArchived, completion);
            return completion.Task;
        }
    }

    private async Task DeleteCatalogProjectOwnedAsync(string projectId, string projectPath, string confirmedName,
        bool expectedArchived, TaskCompletionSource<string> completion)
    {
        try
        {
            completion.TrySetResult(await _runtime.DeleteOwnedCatalogProjectAsync(projectId, projectPath, confirmedName, expectedArchived,
                sessionId => { lock (_gate) return IsOwnerBusy(sessionId) || Asks.List(sessionId).Head is not null; })
                .ConfigureAwait(false));
        }
        catch (Exception) { completion.TrySetResult("delete_unconfirmed"); }
        finally { lock (_gate) { if (ReferenceEquals(_deleteWork, completion.Task)) _deleteWork = null; } }
    }

    /// <summary>Reserves one exact-session journal deletion. Caller cancellation is owned by the waiting bridge only.</summary>
    /// <remarks>Refuses retained commands and refuses active runtime sessions rather than terminating a run or losing queued work.</remarks>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="projectId">Exact project identity, or null for global scope.</param>
    /// <param name="workspacePath">Exact catalog workspace path.</param>
    /// <param name="confirmedTitle">User-confirmed current title.</param>
    /// <returns>ok, busy, closed, session_in_use, session_missing, has_children, or delete_unconfirmed.</returns>
    /// <exception cref="ArgumentException">A required identity or confirmation is blank.</exception>
    public Task<string> DeleteCatalogSessionAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedTitle);
        TaskCompletionSource<string> completion;
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult("closed");
            if (_deleteWork is not null) return Task.FromResult("busy");
            if (_active.ContainsKey(sessionId) || _steering.Contains(sessionId) || _compacting.Contains(sessionId)
                || _abortingRuns.Contains(sessionId) || _remoteControlling.ContainsKey(sessionId)
                || _operations.Values.Any(operation => !operation.Released && string.Equals(operation.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult("busy");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = completion.Task;
            _ = DeleteCatalogSessionOwnedAsync(sessionId, projectId, workspacePath, confirmedTitle, completion);
        }
        return completion.Task;
    }

    /// <summary>
    /// Deletes an exact confirmed session with every session below it (<see cref="SessionLineage.Descendants"/>), in
    /// any project, chat or space; never a project or its files.
    /// </summary>
    /// <remarks>Reserves command admission until completion. Nothing is deleted while one of the sessions is at work,
    /// or when its descendants are no longer the ones that were shown. A failure after the first removal can leave some
    /// sessions deleted; it must not be retried automatically.</remarks>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="projectId">Exact project identity, or null for global scope.</param>
    /// <param name="workspacePath">Exact catalog workspace path.</param>
    /// <param name="confirmedTitle">User-confirmed current title.</param>
    /// <param name="expectedDescendants">The descendants the user saw and confirmed.</param>
    /// <returns>What was deleted; see <see cref="OwnedSessionTreeDeletion"/> for the statuses.</returns>
    /// <exception cref="ArgumentException">A required identity or confirmation is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="expectedDescendants"/> is null.</exception>
    public Task<OwnedSessionTreeDeletion> DeleteCatalogSessionTreeAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle,
        IReadOnlyCollection<string> expectedDescendants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedTitle);
        ArgumentNullException.ThrowIfNull(expectedDescendants);
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult(OwnedSessionTreeDeletion.Refused("closed"));
            if (_deleteWork is not null) return Task.FromResult(OwnedSessionTreeDeletion.Refused("busy"));
            var completion = new TaskCompletionSource<OwnedSessionTreeDeletion>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reservation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = reservation.Task;
            _ = DeleteCatalogSessionTreeOwnedAsync(sessionId, projectId, workspacePath, confirmedTitle, [.. expectedDescendants], completion, reservation);
            return completion.Task;
        }
    }

    private async Task DeleteCatalogSessionTreeOwnedAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle,
        string[] expectedDescendants, TaskCompletionSource<OwnedSessionTreeDeletion> completion, TaskCompletionSource<string> reservation)
    {
        try
        {
            completion.TrySetResult(await _runtime.DeleteOwnedCatalogSessionTreeAsync(sessionId, projectId, workspacePath, confirmedTitle, expectedDescendants,
                OwnerBusyOrAsking).ConfigureAwait(false));
        }
        // Some journals may already have been removed. The caller must inspect, never retry automatically.
        catch (Exception) { completion.TrySetResult(OwnedSessionTreeDeletion.Refused("delete_unconfirmed")); }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_deleteWork, reservation.Task)) _deleteWork = null; }
            reservation.TrySetResult(completion.Task.IsCompletedSuccessfully ? completion.Task.Result.Status : "delete_unconfirmed");
        }
    }

    /// <summary>Tells which of these sessions something goes on in: a run, a queue, a question, a job, background work.</summary>
    /// <param name="sessionIds">The sessions to look at.</param>
    /// <returns>Those at work; a deletion of several sessions is refused while one of them is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionIds"/> is null.</exception>
    public Task<IReadOnlySet<string>> ReadSessionsAtWorkAsync(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        return _runtime.ReadSessionsAtWorkAsync(sessionIds, OwnerBusyOrAsking);
    }

    private async Task DeleteCatalogSessionOwnedAsync(string sessionId, string? projectId, string workspacePath,
        string confirmedTitle, TaskCompletionSource<string> completion)
    {
        try
        {
            completion.TrySetResult(await _runtime.DeleteOwnedCatalogSessionAsync(sessionId, projectId, workspacePath, confirmedTitle)
                .ConfigureAwait(false));
        }
        catch (Exception)
        {
            // The exact journal may already have been removed. The caller must inspect, never retry automatically.
            completion.TrySetResult("delete_unconfirmed");
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_deleteWork, completion.Task)) _deleteWork = null; }
        }
    }
}
