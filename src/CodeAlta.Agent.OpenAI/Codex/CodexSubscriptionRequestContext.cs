#pragma warning disable OPENAI001, SCME0001

using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Runtime;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class CodexSubscriptionRequestContext
{
    public CodexSubscriptionRequestContext(
        string sessionId,
        AgentRunId runId,
        string requestKind,
        DateTimeOffset startedAt,
        string? installationId,
        CodexTurnState turnState,
        string? cacheKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKind);
        ArgumentNullException.ThrowIfNull(turnState);

        SessionId = sessionId.Trim();
        CacheKey = string.IsNullOrWhiteSpace(cacheKey) ? SessionId : cacheKey.Trim();
        RunId = runId;
        RequestKind = requestKind.Trim();
        StartedAt = startedAt;
        InstallationId = string.IsNullOrWhiteSpace(installationId) ? null : installationId.Trim();
        TurnState = turnState;
        ClientMetadata = CreateClientMetadata();
        CompatibilityHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The service takes the affinity of its prompt cache from this header: a session that was forked names the
            // session it comes from here, and itself in the thread and in the metadata of the turn.
            ["session-id"] = CacheKey,
            ["thread-id"] = SessionId,
            ["x-client-request-id"] = SessionId,
            ["x-codex-turn-metadata"] = ClientMetadata["x-codex-turn-metadata"],
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public string SessionId { get; }

    public string CacheKey { get; }

    public AgentRunId RunId { get; }

    public string RequestKind { get; }

    public DateTimeOffset StartedAt { get; }

    public string? InstallationId { get; }

    public CodexTurnState TurnState { get; }

    public IReadOnlyDictionary<string, string> ClientMetadata { get; }

    public IReadOnlyDictionary<string, string> CompatibilityHeaders { get; }

    public void ApplyClientMetadata(CreateResponseOptions options, bool includeTurnState)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var pair in ClientMetadata)
        {
            options.Patch.Set(
                Encoding.UTF8.GetBytes($"$.client_metadata.{pair.Key}"),
                CreateJsonStringData(pair.Value));
        }

        if (includeTurnState && TurnState.TryGetCapturedState(out var turnState))
        {
            options.Patch.Set(
                "$.client_metadata.x-codex-turn-state"u8,
                CreateJsonStringData(turnState));
        }
    }

    private FrozenDictionary<string, string> CreateClientMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["session_id"] = SessionId,
            ["thread_id"] = SessionId,
            ["turn_id"] = RunId.Value,
            ["x-codex-turn-metadata"] = CreateTurnMetadataJson(),
        };
        if (InstallationId is not null)
        {
            metadata["x-codex-installation-id"] = InstallationId;
        }

        return metadata.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private string CreateTurnMetadataJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id"u8, SessionId);
            writer.WriteString("thread_id"u8, SessionId);
            writer.WriteString("turn_id"u8, RunId.Value);
            writer.WriteString("request_kind"u8, RequestKind);
            writer.WriteNumber("turn_started_at_unix_ms"u8, StartedAt.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static BinaryData CreateJsonStringData(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStringValue(value);
        }

        return BinaryData.FromBytes(stream.GetBuffer().AsMemory(0, checked((int)stream.Length)));
    }
}
