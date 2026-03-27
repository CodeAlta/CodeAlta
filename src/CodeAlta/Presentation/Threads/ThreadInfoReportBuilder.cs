using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Presentation.Threads;

internal static class ThreadInfoReportBuilder
{
    public static ThreadInfoReport Build(
        WorkThreadDescriptor thread,
        string backendName,
        string? modelName,
        AgentReasoningEffort? reasoningEffort,
        AgentSessionMetadata? metadata,
        IReadOnlyList<AgentEvent>? history,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendName);

        var createdAt = metadata?.CreatedAt ?? thread.CreatedAt;
        var startedAt = thread.StartedAt ?? createdAt;
        var lastUpdatedAt = metadata?.UpdatedAt ?? thread.UpdatedAt;
        var elapsed = now >= startedAt ? now - startedAt : TimeSpan.Zero;

        return new ThreadInfoReport(
            ThreadTitle: thread.Title,
            BackendName: backendName,
            BackendSessionId: thread.BackendSessionId,
            WorkingDirectory: thread.WorkingDirectory,
            ModelName: modelName,
            ReasoningEffort: reasoningEffort,
            CreatedAt: createdAt,
            StartedAt: startedAt,
            LastUpdatedAt: lastUpdatedAt,
            Elapsed: elapsed,
            UserMessageCount: CountMessages(history, AgentContentKind.User),
            AssistantMessageCount: CountMessages(history, AgentContentKind.Assistant),
            StorageLocation: ProbeStorageLocation(metadata?.WorkspacePath),
            BackendFacts: BuildBackendFacts(metadata?.Details));
    }

    private static int? CountMessages(IReadOnlyList<AgentEvent>? history, AgentContentKind kind)
    {
        if (history is null)
        {
            return null;
        }

        return history.Count(@event => @event is AgentContentCompletedEvent completed && completed.Kind == kind);
    }

    private static ThreadInfoStorageLocation? ProbeStorageLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return new ThreadInfoStorageLocation(
                path,
                ThreadInfoStorageKind.File,
                new FileInfo(path).Length);
        }

        if (Directory.Exists(path))
        {
            return new ThreadInfoStorageLocation(
                path,
                ThreadInfoStorageKind.Directory);
        }

        return new ThreadInfoStorageLocation(
            path,
            ThreadInfoStorageKind.MissingPath);
    }

    private static IReadOnlyList<ThreadInfoFact> BuildBackendFacts(AgentSessionMetadataDetails? details)
    {
        if (details is null)
        {
            return [];
        }

        var facts = new List<ThreadInfoFact>();
        switch (details)
        {
            case CodexSessionMetadataDetails codex:
                AddFact(facts, "Model provider", codex.ModelProvider);
                AddFact(facts, "Source", codex.Source);
                AddFact(facts, "Status", codex.Status);
                AddFact(facts, "Persistence", codex.IsEphemeral ? "Ephemeral" : "Persisted on disk");
                AddFact(facts, "Thread name", codex.ThreadName);
                break;

            case CopilotSessionMetadataDetails copilot:
                AddFact(facts, "Remote session", copilot.IsRemote ? "Yes" : "No");
                break;
        }

        return facts;
    }

    private static void AddFact(List<ThreadInfoFact> facts, string label, string? value)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        facts.Add(new ThreadInfoFact(label, value));
    }
}
