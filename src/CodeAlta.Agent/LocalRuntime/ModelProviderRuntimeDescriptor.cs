using System.Text.Json;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Describes a configured local-runtime provider.
/// </summary>
public sealed record ModelProviderRuntimeDescriptor
{
    /// <summary>
    /// Gets or initializes the protocol family.
    /// </summary>
    public required string ProtocolFamily { get; init; }

    /// <summary>
    /// Gets or initializes the stable provider key.
    /// </summary>
    public required string ProviderKey { get; init; }

    /// <summary>
    /// Gets or initializes the display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets or initializes the transport kind.
    /// </summary>
    public required LocalAgentTransportKind TransportKind { get; init; }

    /// <summary>
    /// Gets or initializes the base URI when applicable.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Gets or initializes whether this is the default provider for its backend.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Gets or initializes the provider capability profile.
    /// </summary>
    public LocalAgentProviderProfile? Profile { get; init; }

    /// <summary>
    /// Gets or initializes normalized compaction settings for the provider.
    /// </summary>
    public LocalAgentCompactionSettings? Compaction { get; init; }

    /// <summary>
    /// Gets or initializes non-secret provider metadata.
    /// </summary>
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// Identifies the provider transport family.
/// </summary>
public enum LocalAgentTransportKind
{
    /// <summary>
    /// OpenAI-compatible Chat/Completions.
    /// </summary>
    OpenAIChatCompletions,

    /// <summary>
    /// OpenAI Responses.
    /// </summary>
    OpenAIResponses,

    /// <summary>
    /// Anthropic Messages.
    /// </summary>
    AnthropicMessages,

    /// <summary>
    /// Google Gemini Developer API.
    /// </summary>
    GoogleGeminiApi,

    /// <summary>
    /// Google Vertex AI.
    /// </summary>
    GoogleVertexAI,
}

/// <summary>
/// Captures compatibility features for a configured provider.
/// </summary>
public sealed record LocalAgentProviderProfile
{
    /// <summary>
    /// Gets or initializes whether the provider supports a developer role.
    /// </summary>
    public bool SupportsDeveloperRole { get; init; } = true;

    /// <summary>
    /// Gets or initializes whether the provider supports the store parameter.
    /// </summary>
    public bool SupportsStore { get; init; }

    /// <summary>
    /// Gets or initializes whether the provider supports reasoning-effort controls.
    /// </summary>
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>
    /// Gets or initializes whether the provider supports parallel tool-call controls.
    /// </summary>
    public bool SupportsParallelToolCalls { get; init; } = true;

    /// <summary>
    /// Gets or initializes whether usage can appear during streaming.
    /// </summary>
    public bool StreamsUsage { get; init; }

    /// <summary>
    /// Gets or initializes the max-token field name.
    /// </summary>
    public string MaxTokensFieldName { get; init; } = "max_completion_tokens";

    /// <summary>
    /// Gets or initializes the reasoning field names in priority order.
    /// </summary>
    public IReadOnlyList<string> ReasoningFieldNames { get; init; } = [];

    /// <summary>
    /// Gets or initializes the assistant-message field name used to replay reasoning content back to the provider.
    /// </summary>
    public string? ReasoningInputFieldName { get; init; }

    /// <summary>
    /// Gets or initializes whether tool results require a synthetic assistant turn before the next user turn.
    /// </summary>
    public bool RequiresAssistantAfterToolResult { get; init; }

    /// <summary>
    /// Gets or initializes whether tool results require the tool name field.
    /// </summary>
    public bool RequiresToolResultName { get; init; }

    /// <summary>
    /// Gets or initializes whether the provider supports cache-control metadata.
    /// </summary>
    public bool SupportsCacheControl { get; init; }

    /// <summary>
    /// Gets or initializes whether the provider preserves thought signatures.
    /// </summary>
    public bool SupportsThoughtSignatures { get; init; }

    /// <summary>
    /// Gets or initializes whether the provider supports strict tool schemas.
    /// </summary>
    public bool SupportsStrictTools { get; init; }

    /// <summary>
    /// Gets or initializes the provider-specific thinking/reasoning format name, when applicable.
    /// </summary>
    public string? ThinkingFormat { get; init; }

    /// <summary>
    /// Gets or initializes additional provider-specific flags.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Flags { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or initializes per-tool availability overrides for the local built-in tool set.
    /// </summary>
    /// <remarks>
    /// Use this to explicitly enable or disable named built-in tools for a provider when the
    /// default provider-based policy is not sufficient.
    /// </remarks>
    public IReadOnlyDictionary<string, bool>? BuiltInToolOverrides { get; init; }
}
