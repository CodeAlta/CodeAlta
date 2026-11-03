using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Node.Tests;

/// <summary>Sessions written into the journals of a host, as a turn of a provider writes them, without running a model.</summary>
internal static class NodeSessionJournals
{
    internal static readonly ModelProviderId Provider = new("fleet-fixture");

    /// <summary>A session of the default space in a folder.</summary>
    internal static async Task<SessionViewDescriptor> AddSessionAsync(CodeAltaHost host, string id, string path, CancellationToken token, string title = "A chat")
    {
        var created = DateTimeOffset.UtcNow;
        var descriptor = new SessionViewDescriptor
        {
            SessionId = id, Kind = SessionViewKind.GlobalSession, ProviderId = Provider.Value, ProviderKey = Provider.Value, WorkingDirectory = path, Title = title,
            CreatedAt = created, UpdatedAt = created, SpaceId = SpaceDescriptor.DefaultId,
        };
        await host.SessionViewCatalog.JournalStore.EnsureHeaderAsync(descriptor, token);
        await host.SessionViewCatalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = id, ProviderId = Provider, ProtocolFamily = Provider.Value, ProviderKey = Provider.Value, WorkingDirectory = path, Title = title,
            CreatedAt = created, UpdatedAt = created,
        }, token);
        await host.AgentSessionCatalog.InvalidateAsync(token);
        return descriptor;
    }

    /// <summary>One turn: a prompt, a tool call, the usage of one request to the model, and its end.</summary>
    internal static async Task AddTurnAsync(CodeAltaHost host, string id, string runId, long input, long output, CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow;
        var run = new AgentRunId(runId);
        await host.SessionViewCatalog.JournalStore.CreateSessionStore().AppendEventsAsync(Provider.Value, Provider.Value, id, [
            new AgentContentCompletedEvent(Provider, id, at, run, AgentContentKind.User, "prompt:" + runId, null, "Hello"),
            new AgentActivityEvent(Provider, id, at, run, AgentActivityKind.ToolCall, AgentActivityPhase.Started, "tool:" + runId, null, "read_file", null),
            new AgentActivityEvent(Provider, id, at.AddSeconds(1), run, AgentActivityKind.ToolCall, AgentActivityPhase.Completed, "tool:" + runId, null, "read_file", null),
            new AgentSessionUpdateEvent(Provider, id, at.AddSeconds(2), run, AgentSessionUpdateKind.UsageUpdated, null,
                Usage: new(LastOperation: new AgentOperationUsageSnapshot(InputTokens: input, OutputTokens: output))),
            new AgentSessionUpdateEvent(Provider, id, at.AddSeconds(3), run, AgentSessionUpdateKind.Idle, null),
        ], token);
    }
}
