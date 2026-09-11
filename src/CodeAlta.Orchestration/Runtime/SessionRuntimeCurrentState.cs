using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Immutable point-in-time facts known to this runtime, not provider quiescence or recoverable history.</summary>
/// <param name="RuntimeInstanceId">Identity of this runtime instance, independent of Display epochs.</param>
/// <param name="SessionId">The queried durable session identifier.</param>
/// <param name="CoordinatorTransitionInProgress">Whether coordinator creation/replacement/detachment has a retained transition.</param>
/// <param name="Entry">The observed runtime entry, or null when none is retained. Absence does not mean idle, completed, or no durable session.</param>
public sealed record SessionRuntimeCurrentState(Guid RuntimeInstanceId, string SessionId,
    bool CoordinatorTransitionInProgress, SessionRuntimeCurrentEntry? Entry);

/// <summary>
/// Immutable captured entry facts. No active run recorded does not prove provider inactivity.
/// Queue depth is unknown: only the in-memory drain flag is captured. Configuration is runtime-captured,
/// not verified provider-effective settings. This value carries no effect acknowledgement or state revision.
/// </summary>
/// <param name="AttachmentGeneration">Existing attachment ordinal, scoped by runtime instance. Identifies replacement, not state ordering.</param>
/// <param name="IsTerminated">Whether the runtime entry observed a Shutdown event, not successful run completion.</param>
/// <param name="IsRetiring">Whether attachment handle admission was closed at capture.</param>
/// <param name="ActiveRunId">Recorded active run identifier, or null when none is recorded.</param>
/// <param name="QueueDrainInProgress">Whether the entry has a queue submission drain in progress.</param>
/// <param name="ProviderId">Captured provider identifier.</param>
/// <param name="ProviderKey">Captured provider configuration key, not credentials.</param>
/// <param name="ModelId">Captured model setting, when supplied.</param>
/// <param name="ReasoningEffort">Captured reasoning setting, when supplied.</param>
/// <param name="AgentPromptId">Captured coordinator prompt identifier.</param>
/// <param name="PendingAgentPromptId">Separately pending prompt selection, not yet the captured coordinator setting.</param>
public sealed record SessionRuntimeCurrentEntry(long AttachmentGeneration, bool IsTerminated, bool IsRetiring,
    string? ActiveRunId, bool QueueDrainInProgress, string ProviderId, string ProviderKey, string? ModelId,
    AgentReasoningEffort? ReasoningEffort, string? AgentPromptId, string? PendingAgentPromptId);
