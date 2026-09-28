namespace CodeAlta.Agent.Runtime;

// Strict, revision-bound observation used before changing the durable provider selection.
internal sealed record AgentTransferSnapshot(
    AgentSessionSummary Summary, AgentSessionState State,
    IReadOnlyList<AgentEvent> History, AgentHistoryRevision Revision);
