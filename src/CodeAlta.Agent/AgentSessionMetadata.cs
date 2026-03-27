using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// Describes a stored or active session known to a backend.
/// </summary>
/// <param name="SessionId">The backend session identifier.</param>
/// <param name="CreatedAt">The time the session was created.</param>
/// <param name="UpdatedAt">The time the session was last updated.</param>
/// <param name="Summary">Optional session summary or preview text.</param>
/// <param name="Context">Optional directory/repo context.</param>
/// <param name="WorkspacePath">Optional backend-managed workspace path for the session.</param>
/// <param name="Details">Optional backend-specific metadata details.</param>
public sealed record AgentSessionMetadata(
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary = null,
    AgentSessionContext? Context = null,
    string? WorkspacePath = null,
    AgentSessionMetadataDetails? Details = null);

/// <summary>
/// Base type for backend-specific session metadata details.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CodexSessionMetadataDetails), "codex")]
[JsonDerivedType(typeof(CopilotSessionMetadataDetails), "copilot")]
public abstract record AgentSessionMetadataDetails;

/// <summary>
/// Codex-specific session metadata details.
/// </summary>
/// <param name="ModelProvider">The model provider reported by Codex when available.</param>
/// <param name="Source">The backend-reported origin for the thread.</param>
/// <param name="Status">The backend-reported runtime status.</param>
/// <param name="IsEphemeral">Whether the thread is ephemeral.</param>
/// <param name="ThreadName">The optional user-facing thread title reported by Codex.</param>
public sealed record CodexSessionMetadataDetails(
    string? ModelProvider = null,
    string? Source = null,
    string? Status = null,
    bool IsEphemeral = false,
    string? ThreadName = null)
    : AgentSessionMetadataDetails;

/// <summary>
/// Copilot-specific session metadata details.
/// </summary>
/// <param name="IsRemote">Whether the session is running remotely.</param>
public sealed record CopilotSessionMetadataDetails(
    bool IsRemote = false)
    : AgentSessionMetadataDetails;

