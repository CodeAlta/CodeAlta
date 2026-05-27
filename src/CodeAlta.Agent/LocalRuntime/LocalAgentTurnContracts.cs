using System.Text.Json;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Provides cached or probed model metadata for a configured provider. Turn execution does not require this contract.
/// </summary>
public interface IModelProviderModelCatalog
{
    /// <summary>
    /// Lists models available to the provider implementation.
    /// </summary>
    /// <param name="provider">The configured provider runtime descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The available models.</returns>
    Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        ModelProviderRuntimeDescriptor provider,
        CancellationToken cancellationToken = default);
}

internal interface ILocalAgentProviderSessionCleanup
{
    ValueTask DisposeProviderSessionAsync(string sessionId);
}

internal sealed record LocalAgentTurnFailure(
    string Message,
    bool IsContextOverflow);

internal sealed class LocalAgentTurnExecutionException : Exception
{
    public LocalAgentTurnExecutionException(LocalAgentTurnFailure failure, Exception? innerException = null)
        : base(failure?.Message, innerException)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public LocalAgentTurnFailure Failure { get; }
}

/// <summary>
/// Represents a single provider turn request.
/// </summary>
public sealed record LocalAgentTurnRequest
{
    /// <summary>
    /// Gets or initializes the configured provider descriptor.
    /// </summary>
    public required ModelProviderRuntimeDescriptor Provider { get; init; }

    /// <summary>
    /// Gets or initializes the backend identifier.
    /// </summary>
    public required AgentBackendId BackendId { get; init; }

    /// <summary>
    /// Gets or initializes the local session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the active run identifier.
    /// </summary>
    public required AgentRunId RunId { get; init; }

    /// <summary>
    /// Gets or initializes the model identifier.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets or initializes the resolved model metadata when available.
    /// </summary>
    public AgentModelInfo? ModelInfo { get; init; }

    /// <summary>
    /// Gets or initializes the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the effective system message.
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// Gets or initializes the effective developer instructions.
    /// </summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>
    /// Gets or initializes the requested reasoning effort.
    /// </summary>
    public AgentReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// Gets or initializes the maximum number of output tokens the provider should generate when supported.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Gets or initializes the replayable conversation.
    /// </summary>
    public required IReadOnlyList<LocalAgentConversationMessage> Conversation { get; init; }

    /// <summary>
    /// Gets or initializes the available tool definitions.
    /// </summary>
    public required IReadOnlyList<AgentToolDefinition> Tools { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether provider-native continuation state from earlier turns in this in-memory session may be reused.
    /// </summary>
    public bool CanUseProviderContinuation { get; init; }

    /// <summary>
    /// Gets or initializes the persisted local session state.
    /// </summary>
    public required LocalAgentSessionState State { get; init; }
}

/// <summary>
/// Represents a provider turn response.
/// </summary>
public sealed record LocalAgentTurnResponse
{
    /// <summary>
    /// Gets or initializes the final assistant message.
    /// </summary>
    public required LocalAgentConversationMessage AssistantMessage { get; init; }

    /// <summary>
    /// Gets or initializes optional stable content identifiers aligned with <see cref="AssistantMessage"/> parts.
    /// Entries may be <see langword="null" /> for parts that do not map to timeline content.
    /// </summary>
    public IReadOnlyList<string?>? AssistantPartContentIds { get; init; }

    /// <summary>
    /// Gets or initializes the latest usage snapshot.
    /// </summary>
    public AgentSessionUsage? Usage { get; init; }

    /// <summary>
    /// Gets or initializes the provider-native session identifier when available.
    /// </summary>
    public string? ProviderSessionId { get; init; }

    /// <summary>
    /// Gets or initializes provider-specific replay hints or diagnostics.
    /// </summary>
    public JsonElement? ProviderState { get; init; }

    /// <summary>
    /// Gets or initializes an optional title candidate.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets or initializes an optional summary candidate.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Represents a best-effort streaming turn update.
/// </summary>
public sealed record LocalAgentTurnDelta
{
    /// <summary>
    /// Gets or initializes the streaming content kind.
    /// </summary>
    public required AgentContentKind Kind { get; init; }

    /// <summary>
    /// Gets or initializes the stable content identifier.
    /// </summary>
    public required string ContentId { get; init; }

    /// <summary>
    /// Gets or initializes the delta text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets or initializes the provider attempt identifier when the delta represents replaceable draft output.
    /// </summary>
    public string? AttemptId { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether this delta is live draft content that can be discarded by a retry.
    /// </summary>
    public bool IsDraft { get; init; } = true;

    /// <summary>
    /// Gets or initializes optional structured delta metadata.
    /// </summary>
    public JsonElement? Details { get; init; }
}

/// <summary>
/// Represents a best-effort provider session update emitted while a turn is running.
/// </summary>
public sealed record LocalAgentTurnSessionUpdate
{
    /// <summary>
    /// Gets or initializes the session update kind.
    /// </summary>
    public required AgentSessionUpdateKind Kind { get; init; }

    /// <summary>
    /// Gets or initializes the user-facing update message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets or initializes optional structured update details.
    /// </summary>
    public JsonElement? Details { get; init; }
}
