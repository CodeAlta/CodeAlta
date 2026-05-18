using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Codex;
using CodeAlta.Agent.CopilotCli;
using XenoAtom.Terminal;

namespace AgentMessageDiagnosticApp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var session = Terminal.Open();
        var app = DiagnosticCliOptions.CreateCommandApp(ExecuteAsync);
        return await app.RunAsync(args).ConfigureAwait(false);
    }

    private static async ValueTask<int> ExecuteAsync(DiagnosticCliOptions options)
    {
        await using var backend = CreateBackend(options.BackendId);
        await backend.StartAsync().ConfigureAwait(false);

        var metadata = await FindSessionMetadataAsync(backend, options.SessionId).ConfigureAwait(false);
        var resumeOptions = new AgentSessionResumeOptions
        {
            WorkingDirectory = metadata?.Context?.Cwd ?? metadata?.WorkspacePath,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };

        await using var session = await backend.ResumeSessionAsync(options.SessionId, resumeOptions).ConfigureAwait(false);
        WriteSessionHeader(options, metadata);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        foreach (var @event in history)
        {
            Terminal.WriteLine(@event.ToJson(options.Indented));
        }

        return 0;
    }

    private static IAgentBackend CreateBackend(AgentBackendId backendId)
    {
        return backendId.Value switch
        {
            "codex" => new CodexAgentBackend(new CodexAgentBackendOptions()),
            "copilot" => new CopilotAgentBackend(new CopilotAgentBackendOptions()),
            _ => throw new InvalidOperationException($"Unsupported backend '{backendId.Value}'."),
        };
    }

    private static async Task<AgentSessionMetadata?> FindSessionMetadataAsync(
        IAgentBackend backend,
        string sessionId)
    {
        await foreach (var session in backend.ListSessionsAsync().ConfigureAwait(false))
        {
            if (string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }

        return null;
    }

    private static void WriteSessionHeader(DiagnosticCliOptions options, AgentSessionMetadata? metadata)
    {
        var payload = new
        {
            recordType = "session",
            backend = options.BackendId.Value,
            sessionId = options.SessionId,
            foundInSessionList = metadata is not null,
            createdAt = metadata?.CreatedAt,
            updatedAt = metadata?.UpdatedAt,
            summary = metadata?.Summary,
            context = metadata?.Context,
            workspacePath = metadata?.WorkspacePath,
        };

        Terminal.WriteLine(JsonSerializer.Serialize(payload));
    }
}
