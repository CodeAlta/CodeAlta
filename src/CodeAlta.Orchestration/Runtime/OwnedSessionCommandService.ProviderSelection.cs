namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Reads enabled providers and exact idle-session selection guards without activating a provider.</summary>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    public Task<OwnedProviderSelectionContext?> GetProviderSelectionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained || Reserved(sessionId)) return Task.FromResult<OwnedProviderSelectionContext?>(null); }
        return _runtime.ReadProviderSelectionAsync(sessionId);
    }

    /// <summary>
    /// Tells whether the provider of a session is available in the space of the session: a session whose provider was
    /// taken out of its space sends nothing until it switches.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when it is, or when the session is not known.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    public Task<bool> IsSessionProviderInSpaceAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _runtime.IsSessionProviderInSpaceAsync(sessionId, cancellationToken);
    }

    /// <summary>Reads the providers a session can use in its space, and how its own provider is named.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the session can use; null when the session is not known.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    public Task<SessionSpaceProviders?> GetSessionSpaceProvidersAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _runtime.GetSessionSpaceProvidersAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// Asks the provider of a session to stop one of its background tasks. The end of the task is told by the
    /// state of the session; a task that already ended is not an error.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="taskId">The identity of the task, as the state of the session lists it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>ok</c> when the provider took the request, <c>unavailable</c> when the session runs no such task, <c>closed</c> when the owner is closing.</returns>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    public async Task<string> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate) { if (_closed || _retained) return "closed"; }
        try { return await _runtime.StopBackgroundTaskAsync(sessionId, taskId, cancellationToken).ConfigureAwait(false) ? "ok" : "unavailable"; }
        catch (ObjectDisposedException) { return "closed"; }
    }

    /// <summary>Saves an idle provider selection. Caller disconnect does not cancel admitted host work.</summary>
    /// <remarks>Uses the exclusive catalog-mutation reservation shared with deletion. Send/queue admission
    /// and shutdown already honor and drain this reservation. No automatic retry is performed.</remarks>
    /// <exception cref="ArgumentNullException">The expected observation is null.</exception>
    public Task<string> SelectProviderAsync(OwnedProviderSelectionContext expected, string providerKey)
        => SelectProviderAsync(expected, providerKey, carryOver: null);

    // carryOver: the model, effort and permission mode a fallback gives the session on the provider.
    internal Task<string> SelectProviderAsync(OwnedProviderSelectionContext expected, string providerKey, Catalog.SessionViewProviderCarryOver? carryOver)
    {
        ArgumentNullException.ThrowIfNull(expected);
        TaskCompletionSource<string> completion;
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult("closed");
            if (Asks.List(expected.SessionId).Head is not null) return Task.FromResult("interaction_pending");
            if (_deleteWork is not null || _active.ContainsKey(expected.SessionId) || _steering.Contains(expected.SessionId)
                || _compacting.Contains(expected.SessionId) || _abortingRuns.Contains(expected.SessionId) || _queueing.Contains(expected.SessionId)
                || _remoteControlling.ContainsKey(expected.SessionId)
                || _operations.Values.Any(operation => !operation.Released && operation.SessionId == expected.SessionId)
                || _queues.Values.Any(operation => !operation.Released && operation.Request.SessionId == expected.SessionId)) return Task.FromResult("busy");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = completion.Task;
            _ = SelectProviderOwnedAsync(expected, providerKey, carryOver, completion);
        }
        return completion.Task;
    }

    // Under _gate: whether a send, a steer, a queue or another command of the session still holds it.
    private bool IsSessionHeld(string sessionId)
        => _deleteWork is not null || _active.ContainsKey(sessionId) || _steering.Contains(sessionId)
           || _compacting.Contains(sessionId) || _abortingRuns.Contains(sessionId) || _queueing.Contains(sessionId)
           || _remoteControlling.ContainsKey(sessionId)
           || _operations.Values.Any(operation => !operation.Released && operation.SessionId == sessionId)
           || _queues.Values.Any(operation => !operation.Released && operation.Request.SessionId == sessionId);

    private async Task SelectProviderOwnedAsync(OwnedProviderSelectionContext expected, string target,
        Catalog.SessionViewProviderCarryOver? carryOver, TaskCompletionSource<string> completion)
    {
        try { completion.TrySetResult(await _runtime.SelectOwnedProviderAsync(expected, target, carryOver).ConfigureAwait(false)); }
        catch (Exception) { completion.TrySetResult("selection_unconfirmed"); }
        finally { lock (_gate) { if (ReferenceEquals(_deleteWork, completion.Task)) _deleteWork = null; } }
    }
}
