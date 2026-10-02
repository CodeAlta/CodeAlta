namespace CodeAlta.Agent.Runtime;

// A prepared candidate is not an IAgentSession and intentionally exposes no send, event,
// attachment or activation API. A durable transfer decision must precede any such API.
internal sealed class AgentPreparedTransfer(
    AgentSession candidate,
    AgentSessionSummary originalSummary,
    AgentSessionState originalState,
    AgentSessionSummary targetSummary,
    AgentSessionState targetState,
    AgentHistoryRevision revision) : IAsyncDisposable
{
    internal AgentSessionSummary OriginalSummary { get; } = originalSummary;
    internal AgentSessionState OriginalState { get; } = originalState;
    internal AgentSessionSummary TargetSummary { get; } = targetSummary;
    internal AgentSessionState TargetState { get; } = targetState;
    internal AgentHistoryRevision Revision { get; } = revision;

    // AgentSession already owns idempotent asynchronous disposal and never writes on idle disposal.
    public ValueTask DisposeAsync() => candidate.DisposeAsync();
}

