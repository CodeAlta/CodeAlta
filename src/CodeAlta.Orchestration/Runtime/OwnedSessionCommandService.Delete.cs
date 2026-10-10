namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
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
                || _abortingRuns.Contains(sessionId) || _queueing.Contains(sessionId) || _remoteControlling.ContainsKey(sessionId)
                || _operations.Values.Any(operation => !operation.Released && string.Equals(operation.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                || _queues.Values.Any(operation => !operation.Released && string.Equals(operation.Request.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult("busy");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = completion.Task;
            _ = DeleteCatalogSessionOwnedAsync(sessionId, projectId, workspacePath, confirmedTitle, completion);
        }
        return completion.Task;
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
