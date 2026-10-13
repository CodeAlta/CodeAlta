namespace CodeAlta.Agent.Runtime;

/// <summary>A system prompt record of a journal, with the prompt it replaced.</summary>
/// <param name="Prompt">The record.</param>
/// <param name="Previous">The system prompt record before it in the journal; null for the first prompt of a session.</param>
/// <param name="PreviousOffset">The starting byte offset of <paramref name="Previous"/>; null when there is none.</param>
public sealed record AgentSystemPromptRecord(AgentSystemPromptEvent Prompt, AgentSystemPromptEvent? Previous, long? PreviousOffset);
