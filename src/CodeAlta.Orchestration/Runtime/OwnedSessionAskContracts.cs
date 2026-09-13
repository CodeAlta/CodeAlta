using CodeAlta.Agent;
using CodeAlta.LiveTool;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Scalar description of an owner-retained ask handle; never sufficient by itself to authorize a response.</summary>
public sealed record OwnedAskHandle(Guid OperationId, Guid RuntimeInstanceId, long AttachmentGeneration,
    string ProviderId, string SessionId, string RunId, string AskId, long ResponseGeneration);

/// <summary>One pending original ask and its current claim state.</summary>
public sealed record OwnedAskHead(OwnedAskHandle Handle, AltaAskRequest Request, string State);

/// <summary>Bounded selected-session view; absence is not an acknowledgment.</summary>
public sealed record OwnedAskPage(OwnedAskHead? Head, OwnedAskDisposition? Latest, bool HasMore);

/// <summary>One explicit user action, copied and validated before admission.</summary>
public sealed record OwnedAskAction(Guid ActionId, OwnedAskHandle Handle, IReadOnlyList<AltaAskAnswer> Answers);

/// <summary>Retained backend evidence for an original action, not transport acknowledgment or provider success.</summary>
public sealed record OwnedAskDisposition(Guid ActionId, OwnedAskHandle Handle, string Status, string? RunId = null);

// Only the private ask owner creates this context and the command owner preserves its reference identity.
internal sealed class OwnedAskSubmission(Guid actionId, string askId)
{
    private string? _positiveRunId;
    internal Guid ActionId { get; } = actionId;
    internal string AskId { get; } = askId;
    internal string? PositiveRunId => Volatile.Read(ref _positiveRunId);
    internal void RecordRunReturned(AgentRunId runId)
    {
        if (OwnedSessionAskTool.Identity(runId.Value)) Interlocked.CompareExchange(ref _positiveRunId, runId.Value, null);
    }
}
