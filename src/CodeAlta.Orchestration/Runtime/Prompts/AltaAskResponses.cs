using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.LiveTool;

/// <summary>The current in-memory response ownership state of a pending ask.</summary>
public enum AltaAskResponseState
{
    /// <summary>A fresh response or local cancellation may claim this attempt.</summary>
    Pending,
    /// <summary>A response owns this head; another response or cancellation cannot take it.</summary>
    Submitting,
    /// <summary>Admission is unresolved; replay and local cancellation are blocked.</summary>
    Indeterminate,
}

/// <summary>An immutable owner-issued identity for one response generation. Not a renderer authorization or durable token.</summary>
public sealed class AltaAskResponseHandle
{
    internal AltaAskResponseHandle(string sessionId, string askId, long generation)
        => (SessionId, AskId, Generation) = (sessionId, askId, generation);

    /// <summary>Gets the exact target session identifier.</summary>
    public string SessionId { get; }
    /// <summary>Gets the exact ask identifier.</summary>
    public string AskId { get; }
    /// <summary>Gets the attempt generation, advanced after definite non-admission.</summary>
    public long Generation { get; }
}

/// <summary>The result of attempting to claim and settle a response.</summary>
public sealed record AltaAskResponseResult
{
    /// <summary>Gets whether this invocation claimed the pending head and invoked its dispatch callback.</summary>
    public required bool Claimed { get; init; }
    /// <summary>Gets admission evidence, or null when the claim was rejected.</summary>
    public SessionPromptResponseResult? DispatchResult { get; init; }
    /// <summary>Gets immutable scalar invalidation failures from committed claim and settlement transitions.</summary>
    public IReadOnlyList<string> NotificationErrors { get; init; } = [];
}

public sealed partial class AltaAskService
{
    /// <inheritdoc />
    public Task<AltaAskResponseResult> RespondAsync(AltaAskResponseHandle handle, Func<Task<SessionPromptResponseResult>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_gate)
        {
            if (FindPendingHead(handle) is not { } entry)
            {
                return Task.FromResult(new AltaAskResponseResult { Claimed = false });
            }
            entry.Snapshot = entry.Snapshot with { ResponseState = AltaAskResponseState.Submitting };
        }
        // Claim precedes notifications, formatting, history and dispatch. No callback executes under ownership.
        var errors = NotifyQueueChanged(handle.SessionId);
        return DispatchAndSettleAsync(handle, dispatch, errors);
    }

    /// <inheritdoc />
    public AltaAskRemovalResult TryCancelResponse(AltaAskResponseHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (FindPendingHead(handle) is null)
            {
                return new AltaAskRemovalResult { Accepted = false };
            }
            RemoveHeadCore(handle.SessionId);
        }
        return new AltaAskRemovalResult { Accepted = true, NotificationErrors = NotifyQueueChanged(handle.SessionId) };
    }

    private PendingAsk? FindPendingHead(AltaAskResponseHandle handle)
        => _queues.TryGetValue(handle.SessionId, out var queue) && queue.Count > 0
            && ReferenceEquals(queue.Peek().Snapshot.ResponseHandle, handle)
            && queue.Peek().Snapshot.ResponseState == AltaAskResponseState.Pending ? queue.Peek() : null;

    private async Task<AltaAskResponseResult> DispatchAndSettleAsync(
        AltaAskResponseHandle handle, Func<Task<SessionPromptResponseResult>> dispatch, IReadOnlyList<string> claimErrors)
    {
        SessionPromptResponseResult result;
        try
        {
            result = await dispatch().ConfigureAwait(false) ?? SessionPromptResponseResult.Indeterminate();
        }
        catch (Exception ex)
        {
            // A callback without phase evidence cannot promise rejection or authorize replay.
            result = SessionPromptResponseResult.Indeterminate(ex.Message);
        }

        lock (_gate)
        {
            // Only this private completion can settle its claim. Public removal/cancel cannot bypass it.
            var entry = _queues[handle.SessionId].Peek();
            if (!ReferenceEquals(entry.Snapshot.ResponseHandle, handle) || entry.Snapshot.ResponseState != AltaAskResponseState.Submitting)
            {
                throw new InvalidOperationException("Ask response ownership changed before settlement.");
            }
            switch (result.Admission)
            {
                case SessionPromptResponseAdmission.Admitted:
                    RemoveHeadCore(handle.SessionId);
                    break;
                case SessionPromptResponseAdmission.DefinitelyNotAdmittedByThisRoute:
                    entry.Snapshot = entry.Snapshot with
                    {
                        ResponseState = AltaAskResponseState.Pending,
                        ResponseHandle = new AltaAskResponseHandle(handle.SessionId, handle.AskId, checked(handle.Generation + 1)),
                    };
                    break;
                default:
                    entry.Snapshot = entry.Snapshot with { ResponseState = AltaAskResponseState.Indeterminate };
                    break;
            }
        }
        var settlementErrors = NotifyQueueChanged(handle.SessionId);
        return new AltaAskResponseResult
        {
            Claimed = true,
            DispatchResult = result,
            NotificationErrors = Array.AsReadOnly(claimErrors.Concat(settlementErrors).ToArray()),
        };
    }

    private void RemoveHeadCore(string sessionId)
    {
        var queue = _queues[sessionId];
        queue.Dequeue();
        if (queue.Count == 0)
        {
            _queues.Remove(sessionId);
        }
    }

    private sealed class PendingAsk(AltaQueuedAsk snapshot)
    {
        public AltaQueuedAsk Snapshot { get; set; } = snapshot;
    }
}
