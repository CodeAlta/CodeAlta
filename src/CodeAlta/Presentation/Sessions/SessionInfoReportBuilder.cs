using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Presentation.Sessions;

internal static class SessionInfoReportBuilder
{
    public static SessionInfoReport Build(
        SessionViewDescriptor session,
        string providerName,
        string? modelName,
        AgentReasoningEffort? reasoningEffort,
        AgentSessionMetadata? metadata,
        IReadOnlyList<AgentEvent>? history,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var createdAt = metadata?.CreatedAt ?? session.CreatedAt;
        var startedAt = session.StartedAt ?? createdAt;
        var lastUpdatedAt = metadata?.UpdatedAt ?? session.UpdatedAt;
        var elapsed = now >= startedAt ? now - startedAt : TimeSpan.Zero;

        return new SessionInfoReport(
            SessionTitle: session.Title,
            ProviderName: providerName,
            SessionId: session.SessionId,
            WorkingDirectory: session.WorkingDirectory,
            ModelName: modelName,
            ReasoningEffort: reasoningEffort,
            CreatedAt: createdAt,
            StartedAt: startedAt,
            LastUpdatedAt: lastUpdatedAt,
            Elapsed: elapsed,
            UserMessageCount: CountMessages(history, AgentContentKind.User),
            AssistantMessageCount: CountMessages(history, AgentContentKind.Assistant),
            StorageLocation: ProbeStorageLocation(metadata?.WorkspacePath),
            ProviderFacts: BuildProviderFacts(metadata?.Details),
            LoadedSkills: BuildLoadedSkills(history));
    }

    private static int? CountMessages(IReadOnlyList<AgentEvent>? history, AgentContentKind kind)
    {
        if (history is null)
        {
            return null;
        }

        return history.Count(@event => @event is AgentContentCompletedEvent completed && completed.Kind == kind);
    }

    private static SessionInfoStorageLocation? ProbeStorageLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return new SessionInfoStorageLocation(
                path,
                SessionInfoStorageKind.File,
                new FileInfo(path).Length);
        }

        if (Directory.Exists(path))
        {
            return new SessionInfoStorageLocation(
                path,
                SessionInfoStorageKind.Directory);
        }

        return new SessionInfoStorageLocation(
            path,
            SessionInfoStorageKind.MissingPath);
    }

    private static IReadOnlyList<SessionInfoFact> BuildProviderFacts(AgentSessionMetadataDetails? details)
    {
        if (details is null)
        {
            return [];
        }

        var facts = new List<SessionInfoFact>();
        switch (details)
        {
            case CodexSessionMetadataDetails codex:
                AddFact(facts, "Model provider", codex.ModelProvider);
                AddFact(facts, "Source", codex.Source);
                AddFact(facts, "Status", codex.Status);
                AddFact(facts, "Persistence", codex.IsEphemeral ? "Ephemeral" : "Persisted on disk");
                AddFact(facts, "Session name", codex.SessionName);
                break;

            case CopilotSessionMetadataDetails copilot:
                AddFact(facts, "Remote session", copilot.IsRemote ? "Yes" : "No");
                break;
        }

        return facts;
    }

    private static IReadOnlyList<AgentLoadedSkillState> BuildLoadedSkills(IReadOnlyList<AgentEvent>? history)
    {
        if (history is null)
        {
            return [];
        }

        var loadedSkills = new Dictionary<string, AgentLoadedSkillState>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawEvent in history.OfType<AgentRawEvent>())
        {
            if (!string.Equals(rawEvent.BackendEventType, "local.skillActivation", StringComparison.Ordinal))
            {
                continue;
            }

            var skill = rawEvent.Raw.ToAgentLoadedSkillState();
            if (skill is null || string.IsNullOrWhiteSpace(skill.Name))
            {
                continue;
            }

            loadedSkills[skill.Name] = skill;
        }

        return loadedSkills.Values
            .OrderBy(static skill => skill.ActivatedAt)
            .ThenBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddFact(List<SessionInfoFact> facts, string label, string? value)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        facts.Add(new SessionInfoFact(label, value));
    }
}
