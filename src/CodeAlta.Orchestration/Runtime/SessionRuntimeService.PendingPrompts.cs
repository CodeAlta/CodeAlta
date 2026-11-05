using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What became of a request to remove a queued prompt.</summary>
public enum QueuedPromptRemoval
{
    /// <summary>The prompt was removed: it will never be sent.</summary>
    Removed,

    /// <summary>The session is taking the prompt: it can no longer be removed.</summary>
    Sending,

    /// <summary>The queue holds no such prompt: it was sent, removed or never queued.</summary>
    NotFound,
}

// The prompts of a session that wait for it, which their sender can still take back: those of its queue, and what was
// steered into its run and the run has not given to the model yet.
public sealed partial class SessionRuntimeService
{
    /// <summary>Reads the prompts that wait in the queue of a session, in the order they will be sent.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The prompts that wait or are being sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<IReadOnlyList<SessionViewQueuedPrompt>> GetQueuedPromptsAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await AdmitAsync<IReadOnlyList<SessionViewQueuedPrompt>>(async () =>
        {
            var local = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false);
            return [.. (local?.QueuedPrompts ?? []).Where(static prompt => SessionViewQueuedPrompt.IsPendingState(prompt.State))];
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a prompt from the queue of a session, unless the session is taking it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="queueItemId">The identifier of the prompt in the queue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What became of the request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="queueItemId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The request is canceled.</exception>
    public async Task<QueuedPromptRemoval> RemoveQueuedPromptAsync(SessionViewDescriptor session, string queueItemId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueItemId);
        return await AdmitAsync(() => RemoveQueuedPromptOwnedBodyAsync(session, queueItemId), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // On the actor of the session, as the drain marks a prompt it takes: the prompt is removed here or sent there, never both.
    private async Task<QueuedPromptRemoval> RemoveQueuedPromptOwnedBodyAsync(SessionViewDescriptor session, string queueItemId)
    {
        using var retentionWork = ClaimRetentionWork(session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId);
        using var lease = HoldForWrite(session.SessionId);
        var actor = GetActorForWork(session.SessionId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var local = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                    var item = local?.QueuedPrompts.FirstOrDefault(prompt => string.Equals(prompt.QueueItemId, queueItemId, StringComparison.Ordinal));
                    if (item is null || !SessionViewQueuedPrompt.IsPendingState(item.State)) return QueuedPromptRemoval.NotFound;
                    if (!string.Equals(item.State, "queued", StringComparison.OrdinalIgnoreCase)) return QueuedPromptRemoval.Sending;
                    var timestamp = DateTimeOffset.UtcNow;
                    item.State = "canceled";
                    item.DrainedAt = timestamp;
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, local!, actorCancellationToken).ConfigureAwait(false);
                    PublishQueueChanged(session.SessionId, local!, item, timestamp, isEnqueued: false);
                    return QueuedPromptRemoval.Removed;
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Takes back what was steered into the run of a session, while the run has not given it to the model.</summary>
    /// <param name="session">The session.</param>
    /// <param name="withdrawalId">The identifier given with the steering input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the input was taken back; <see langword="false"/> when the model has it, or will have it,
    /// or when no run holds it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="withdrawalId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The request is canceled.</exception>
    public async Task<bool> TryWithdrawSteerAsync(SessionViewDescriptor session, string withdrawalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(withdrawalId);
        return await AdmitAsync(() => TryWithdrawSteerOwnedBodyAsync(session, withdrawalId, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryWithdrawSteerOwnedBodyAsync(SessionViewDescriptor session, string withdrawalId, CancellationToken cancellationToken)
    {
        return await WithRetentionWorkAsync(async () =>
        {
            OwnedProviderEventForwarding.Use? handleUse = null;
            try
            {
                var handle = await GetActorForWork(session.SessionId).QueryAsync(
                        _ =>
                        {
                            if (!_entries.TryGetValue(session.SessionId, out var entry) || entry.IsTerminated || !entry.HasActiveRun) return ValueTask.FromResult<AgentSessionHandleId?>(null);
                            handleUse = entry.Attachment.TryAcquireHandleUse();
                            return ValueTask.FromResult<AgentSessionHandleId?>(handleUse is null ? null : entry.SessionHandleId);
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (handle is not { } sessionHandleId) return false;
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handleUse!.Attachment.Cancellation.Token);
                return await _agentHub.TryWithdrawSteerAsync(sessionHandleId, withdrawalId, execution.Token).ConfigureAwait(false);
            }
            catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
            {
                handleUse?.Retain(failure);
                throw;
            }
            finally { handleUse?.Dispose(); }
        }, session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId).ConfigureAwait(false);
    }
}
