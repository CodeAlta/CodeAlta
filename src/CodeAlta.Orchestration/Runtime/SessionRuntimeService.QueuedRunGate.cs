namespace CodeAlta.Orchestration.Runtime;

/// <summary>A prompt that waited in the queue of a session, about to start its turn.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="QueueItemId">The queued prompt: the run a gate admits.</param>
/// <param name="Kind">What queued it, such as <c>queue</c> or the result of a child session.</param>
public sealed record QueuedRunAdmission(string SessionId, string QueueItemId, string Kind);

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Gets or sets what admits a queued prompt when its turn starts, not when it was queued (the budgets of a node,
    /// <c>doc/fleet/observability.md</c>): it may wait, and gives why the turn may not start, or null when it may. A refused
    /// prompt fails without reaching the model and the queue goes on. Null starts each queued prompt at once.
    /// </summary>
    public Func<QueuedRunAdmission, CancellationToken, Task<string?>>? QueuedRunGate { get; set; }

    // Why the queued prompt may not start; null when it may. A gate that fails refuses it, rather than run it unchecked.
    private static async Task<string?> AdmitQueuedAsync(Func<QueuedRunAdmission, CancellationToken, Task<string?>> gate, string sessionId, QueuedPromptDrainWork work)
    {
        try
        {
            return await gate(new QueuedRunAdmission(sessionId, work.Prompt.QueueItemId, work.Prompt.Kind), work.Entry.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "The session stopped while its queued prompt waited to start.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"The queued prompt was not admitted: {exception.Message}";
        }
    }
}
