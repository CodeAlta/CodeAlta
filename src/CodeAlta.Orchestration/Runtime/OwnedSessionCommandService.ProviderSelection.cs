namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Reads enabled providers and exact idle-session selection guards without activating a provider.</summary>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    public Task<OwnedProviderSelectionContext?> GetProviderSelectionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained || _deleteWork is not null) return Task.FromResult<OwnedProviderSelectionContext?>(null); }
        return _runtime.ReadProviderSelectionAsync(sessionId);
    }

    /// <summary>Saves an idle provider selection. Caller disconnect does not cancel admitted host work.</summary>
    /// <remarks>Uses the exclusive catalog-mutation reservation shared with deletion. Send/queue admission
    /// and shutdown already honor and drain this reservation. No automatic retry is performed.</remarks>
    /// <exception cref="ArgumentNullException">The expected observation is null.</exception>
    public Task<string> SelectProviderAsync(OwnedProviderSelectionContext expected, string providerKey)
    {
        ArgumentNullException.ThrowIfNull(expected);
        TaskCompletionSource<string> completion;
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult("closed");
            if (Asks.List(expected.SessionId).Head is not null) return Task.FromResult("interaction_pending");
            if (_deleteWork is not null || _active.ContainsKey(expected.SessionId) || _steering.Contains(expected.SessionId)
                || _compacting.Contains(expected.SessionId) || _abortingRuns.Contains(expected.SessionId) || _queueing.Contains(expected.SessionId)
                || _operations.Values.Any(operation => !operation.Released && operation.SessionId == expected.SessionId)
                || _queues.Values.Any(operation => !operation.Released && operation.Request.SessionId == expected.SessionId)) return Task.FromResult("busy");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = completion.Task;
            _ = SelectProviderOwnedAsync(expected, providerKey, completion);
        }
        return completion.Task;
    }

    private async Task SelectProviderOwnedAsync(OwnedProviderSelectionContext expected, string target, TaskCompletionSource<string> completion)
    {
        try { completion.TrySetResult(await _runtime.SelectOwnedProviderAsync(expected, target).ConfigureAwait(false)); }
        catch (Exception) { completion.TrySetResult("selection_unconfirmed"); }
        finally { lock (_gate) { if (ReferenceEquals(_deleteWork, completion.Task)) _deleteWork = null; } }
    }
}
