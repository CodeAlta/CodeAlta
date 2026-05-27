using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Presentation.Sessions;

internal sealed record SessionInfoReport(
    string SessionTitle,
    string ProviderName,
    string SessionId,
    string WorkingDirectory,
    string? ModelName,
    AgentReasoningEffort? ReasoningEffort,
    DateTimeOffset CreatedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUpdatedAt,
    TimeSpan Elapsed,
    int? UserMessageCount,
    int? AssistantMessageCount,
    SessionInfoStorageLocation? StorageLocation,
    IReadOnlyList<SessionInfoFact> ProviderFacts,
    IReadOnlyList<AgentLoadedSkillState> LoadedSkills);

internal sealed record SessionInfoStorageLocation(
    string Path,
    SessionInfoStorageKind Kind,
    long? SizeBytes = null);

internal sealed record SessionInfoFact(
    string Label,
    string Value);

internal enum SessionInfoStorageKind
{
    File,
    Directory,
    MissingPath,
}
