namespace CodeAlta.Agent.Runtime;

/// <summary>The usage of one request to the model, as the journal of a session records it.</summary>
/// <param name="RunId">The run the request belongs to; null for a record that names none.</param>
/// <param name="Operation">The usage the provider reported for the request.</param>
public sealed record AgentSessionUsageUpdate(string? RunId, AgentOperationUsageSnapshot Operation);
