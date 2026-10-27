using CodeAlta.Agent;
using CodeAlta.LiveTool;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Scalar description of an owner-retained ask handle; never sufficient by itself to authorize a response.</summary>
public sealed record OwnedAskHandle(Guid OperationId, Guid RuntimeInstanceId, long AttachmentGeneration,
    string ProviderId, string SessionId, string RunId, string AskId, long ResponseGeneration);

/// <summary>One pending original ask and its current claim state.</summary>
public sealed record OwnedAskHead(OwnedAskHandle Handle, AltaAskRequest Request, string State)
{
    /// <summary>
    /// Gets whether the ask is shown at once, whatever the turn of its session does: a question of a script of Code Mode
    /// (<c>ask()</c>), which the running turn waits for. Another ask is shown once the turn that asked has ended.
    /// </summary>
    public bool Immediate { get; init; }
}

/// <summary>Bounded selected-session view; absence is not an acknowledgment.</summary>
public sealed record OwnedAskPage(OwnedAskHead? Head, OwnedAskDisposition? Latest, bool HasMore);

/// <summary>One explicit user action, copied and validated before admission.</summary>
/// <param name="ActionId">The identity of this action; a repeat with the same content returns the original outcome.</param>
/// <param name="Handle">The pending ask being answered or cancelled.</param>
/// <param name="Answers">One answer per question; empty for a cancel.</param>
/// <param name="Review">
/// The round of the review the request asks for, as the host composed it from the user's comments and verdict;
/// required for an answer of a request with a review target, absent otherwise and for a cancel.
/// </param>
public sealed record OwnedAskAction(Guid ActionId, OwnedAskHandle Handle, IReadOnlyList<AltaAskAnswer> Answers, string? Review = null);

/// <summary>Retained backend evidence for an original action, not transport acknowledgment or provider success.</summary>
public sealed record OwnedAskDisposition(Guid ActionId, OwnedAskHandle Handle, string Status, string? RunId = null);

// Only the private ask owner creates this context and the command owner preserves its reference identity.
internal sealed class OwnedAskSubmission(Guid actionId, string askId)
{
    private readonly TaskCompletionSource _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _positiveRunId;
    internal Guid ActionId { get; } = actionId;
    internal string AskId { get; } = askId;
    internal string? PositiveRunId => Volatile.Read(ref _positiveRunId);
    /// <summary>Completes with the first positive run identity: the session has the answer from then on.</summary>
    internal Task Delivered => _delivered.Task;
    /// <summary>The provider activated the run that carries the answer as its input; the run goes on.</summary>
    internal void RecordRunStarted(AgentRunId runId) => Record(runId);
    internal void RecordRunReturned(AgentRunId runId) => Record(runId);
    private void Record(AgentRunId runId)
    {
        if (!OwnedSessionAskTool.Identity(runId.Value)) return;
        Interlocked.CompareExchange(ref _positiveRunId, runId.Value, null);
        _delivered.TrySetResult();
    }
}
