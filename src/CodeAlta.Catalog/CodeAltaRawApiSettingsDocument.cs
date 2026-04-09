using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Represents raw-API provider configuration groups.
/// </summary>
public sealed class CodeAltaRawApiSettingsDocument
{
    /// <summary>
    /// Gets or sets global raw-API compaction settings.
    /// </summary>
    [JsonPropertyName("compaction")]
    public CodeAltaRawApiCompactionDocument? Compaction { get; set; }

    /// <summary>
    /// Gets or sets OpenAI-compatible provider settings.
    /// </summary>
    [JsonPropertyName("openai")]
    public CodeAltaOpenAISettingsDocument OpenAI { get; set; } = new();

    /// <summary>
    /// Gets or sets Anthropic provider settings.
    /// </summary>
    [JsonPropertyName("anthropic")]
    public CodeAltaAnthropicSettingsDocument Anthropic { get; set; } = new();

    /// <summary>
    /// Gets or sets Google GenAI provider settings.
    /// </summary>
    [JsonPropertyName("google_genai")]
    public CodeAltaGoogleGenAISettingsDocument GoogleGenAI { get; set; } = new();
}

/// <summary>
/// Represents configured OpenAI-compatible providers.
/// </summary>
public sealed class CodeAltaOpenAISettingsDocument
{
    /// <summary>
    /// Gets or sets provider definitions keyed by stable provider key.
    /// </summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, CodeAltaOpenAIProviderDocument> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents configured Anthropic providers.
/// </summary>
public sealed class CodeAltaAnthropicSettingsDocument
{
    /// <summary>
    /// Gets or sets provider definitions keyed by stable provider key.
    /// </summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, CodeAltaAnthropicProviderDocument> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents configured Google GenAI providers.
/// </summary>
public sealed class CodeAltaGoogleGenAISettingsDocument
{
    /// <summary>
    /// Gets or sets provider definitions keyed by stable provider key.
    /// </summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, CodeAltaGoogleGenAIProviderDocument> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a configurable compatibility profile override for a raw-API provider.
/// </summary>
public sealed class CodeAltaRawApiProviderProfileDocument
{
    /// <summary>
    /// Gets or sets whether the provider supports the developer role.
    /// </summary>
    [JsonPropertyName("supports_developer_role")]
    public bool? SupportsDeveloperRole { get; set; }

    /// <summary>
    /// Gets or sets whether the provider supports the store flag.
    /// </summary>
    [JsonPropertyName("supports_store")]
    public bool? SupportsStore { get; set; }

    /// <summary>
    /// Gets or sets whether the provider supports reasoning effort.
    /// </summary>
    [JsonPropertyName("supports_reasoning_effort")]
    public bool? SupportsReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets whether the provider streams usage information.
    /// </summary>
    [JsonPropertyName("streams_usage")]
    public bool? StreamsUsage { get; set; }

    /// <summary>
    /// Gets or sets whether the provider supports thought signatures.
    /// </summary>
    [JsonPropertyName("supports_thought_signatures")]
    public bool? SupportsThoughtSignatures { get; set; }

    /// <summary>
    /// Gets or sets the provider-specific max-tokens field name.
    /// </summary>
    [JsonPropertyName("max_tokens_field_name")]
    public string? MaxTokensFieldName { get; set; }

    /// <summary>
    /// Gets or sets the provider-specific reasoning field names.
    /// </summary>
    [JsonPropertyName("reasoning_field_names")]
    public List<string>? ReasoningFieldNames { get; set; }
}

/// <summary>
/// Represents configurable raw-API compaction settings.
/// </summary>
public sealed class CodeAltaRawApiCompactionDocument
{
    /// <summary>
    /// Gets or sets whether automatic compaction is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets the usage threshold that triggers compaction.
    /// </summary>
    [JsonPropertyName("trigger_threshold")]
    public double? TriggerThreshold { get; set; }

    /// <summary>
    /// Gets or sets the target usage threshold after compaction.
    /// </summary>
    [JsonPropertyName("target_threshold")]
    public double? TargetThreshold { get; set; }

    /// <summary>
    /// Gets or sets the reserved output tokens.
    /// </summary>
    [JsonPropertyName("reserved_output_tokens")]
    public int? ReservedOutputTokens { get; set; }

    /// <summary>
    /// Gets or sets the reserved overhead tokens.
    /// </summary>
    [JsonPropertyName("reserved_overhead_tokens")]
    public int? ReservedOverheadTokens { get; set; }

    /// <summary>
    /// Gets or sets whether the latest user message should be kept verbatim.
    /// </summary>
    [JsonPropertyName("keep_last_user_message")]
    public bool? KeepLastUserMessage { get; set; }

    /// <summary>
    /// Gets or sets whether split-turn compaction is allowed.
    /// </summary>
    [JsonPropertyName("allow_split_turn")]
    public bool? AllowSplitTurn { get; set; }
}

/// <summary>
/// Represents one configurable raw-API model metadata override.
/// </summary>
public sealed class CodeAltaRawApiModelOverrideDocument
{
    /// <summary>
    /// Gets or sets an optional replacement display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets an optional replacement description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the context-window limit in tokens.
    /// </summary>
    [JsonPropertyName("context_window")]
    public long? ContextWindow { get; set; }

    /// <summary>
    /// Gets or sets the maximum input-token limit in tokens.
    /// </summary>
    [JsonPropertyName("input_token_limit")]
    public long? InputTokenLimit { get; set; }

    /// <summary>
    /// Gets or sets the maximum output-token limit in tokens.
    /// </summary>
    [JsonPropertyName("output_token_limit")]
    public long? OutputTokenLimit { get; set; }

    /// <summary>
    /// Gets or sets the maximum generated-token limit in tokens.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public long? MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets whether the model supports reasoning.
    /// </summary>
    [JsonPropertyName("supports_reasoning")]
    public bool? SupportsReasoning { get; set; }

    /// <summary>
    /// Gets or sets whether the model supports tool calling.
    /// </summary>
    [JsonPropertyName("supports_tool_call")]
    public bool? SupportsToolCall { get; set; }

    /// <summary>
    /// Gets or sets whether the model supports attachments.
    /// </summary>
    [JsonPropertyName("supports_attachments")]
    public bool? SupportsAttachments { get; set; }

    /// <summary>
    /// Gets or sets whether the model supports structured output.
    /// </summary>
    [JsonPropertyName("supports_structured_output")]
    public bool? SupportsStructuredOutput { get; set; }
}

/// <summary>
/// Represents one configured OpenAI-compatible provider.
/// </summary>
public sealed class CodeAltaOpenAIProviderDocument
{
    /// <summary>
    /// Gets or sets the normalized provider key.
    /// </summary>
    [JsonIgnore]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the provider registration is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the user-facing provider display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the API key literal when configured directly.
    /// </summary>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the environment variable used to resolve the API key.
    /// </summary>
    [JsonPropertyName("api_key_env")]
    public string? ApiKeyEnv { get; set; }

    /// <summary>
    /// Gets or sets the base endpoint override.
    /// </summary>
    [JsonPropertyName("base_uri")]
    public string? BaseUri { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenAI organization id.
    /// </summary>
    [JsonPropertyName("organization_id")]
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenAI project id.
    /// </summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets the optional models.dev provider identifier used to enrich model metadata.
    /// </summary>
    [JsonPropertyName("models_dev_provider_id")]
    public string? ModelsDevProviderId { get; set; }

    /// <summary>
    /// Gets or sets whether the Responses backend is enabled for this provider.
    /// </summary>
    [JsonPropertyName("enable_responses")]
    public bool EnableResponses { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the Chat/Completions backend is enabled for this provider.
    /// </summary>
    [JsonPropertyName("enable_chat")]
    public bool EnableChat { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this provider is the default OpenAI Responses provider.
    /// </summary>
    [JsonPropertyName("default_responses")]
    public bool DefaultResponses { get; set; }

    /// <summary>
    /// Gets or sets whether this provider is the default OpenAI Chat provider.
    /// </summary>
    [JsonPropertyName("default_chat")]
    public bool DefaultChat { get; set; }

    /// <summary>
    /// Gets or sets the optional compatibility-profile override.
    /// </summary>
    [JsonPropertyName("profile")]
    public CodeAltaRawApiProviderProfileDocument? Profile { get; set; }

    /// <summary>
    /// Gets or sets the optional compaction override.
    /// </summary>
    [JsonPropertyName("compaction")]
    public CodeAltaRawApiCompactionDocument? Compaction { get; set; }

    /// <summary>
    /// Gets or sets optional per-model metadata overrides.
    /// </summary>
    [JsonPropertyName("model_overrides")]
    public Dictionary<string, CodeAltaRawApiModelOverrideDocument>? ModelOverrides { get; set; }
}

/// <summary>
/// Represents one configured Anthropic provider.
/// </summary>
public sealed class CodeAltaAnthropicProviderDocument
{
    /// <summary>
    /// Gets or sets the normalized provider key.
    /// </summary>
    [JsonIgnore]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the provider registration is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the user-facing provider display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the API key literal when configured directly.
    /// </summary>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the environment variable used to resolve the API key.
    /// </summary>
    [JsonPropertyName("api_key_env")]
    public string? ApiKeyEnv { get; set; }

    /// <summary>
    /// Gets or sets the base endpoint override.
    /// </summary>
    [JsonPropertyName("base_uri")]
    public string? BaseUri { get; set; }

    /// <summary>
    /// Gets or sets the optional models.dev provider identifier used to enrich model metadata.
    /// </summary>
    [JsonPropertyName("models_dev_provider_id")]
    public string? ModelsDevProviderId { get; set; }

    /// <summary>
    /// Gets or sets whether this provider is the backend default.
    /// </summary>
    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets the optional compatibility-profile override.
    /// </summary>
    [JsonPropertyName("profile")]
    public CodeAltaRawApiProviderProfileDocument? Profile { get; set; }

    /// <summary>
    /// Gets or sets the optional compaction override.
    /// </summary>
    [JsonPropertyName("compaction")]
    public CodeAltaRawApiCompactionDocument? Compaction { get; set; }

    /// <summary>
    /// Gets or sets optional per-model metadata overrides.
    /// </summary>
    [JsonPropertyName("model_overrides")]
    public Dictionary<string, CodeAltaRawApiModelOverrideDocument>? ModelOverrides { get; set; }
}

/// <summary>
/// Represents one configured Google GenAI provider.
/// </summary>
public sealed class CodeAltaGoogleGenAIProviderDocument
{
    /// <summary>
    /// Gets or sets the normalized provider key.
    /// </summary>
    [JsonIgnore]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the provider registration is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the user-facing provider display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the API key literal when configured directly.
    /// </summary>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the environment variable used to resolve the API key.
    /// </summary>
    [JsonPropertyName("api_key_env")]
    public string? ApiKeyEnv { get; set; }

    /// <summary>
    /// Gets or sets whether the provider should use Vertex AI.
    /// </summary>
    [JsonPropertyName("use_vertex_ai")]
    public bool UseVertexAI { get; set; }

    /// <summary>
    /// Gets or sets the Google Cloud project for Vertex AI.
    /// </summary>
    [JsonPropertyName("project")]
    public string? Project { get; set; }

    /// <summary>
    /// Gets or sets the Google Cloud location for Vertex AI.
    /// </summary>
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets the base endpoint override.
    /// </summary>
    [JsonPropertyName("base_uri")]
    public string? BaseUri { get; set; }

    /// <summary>
    /// Gets or sets the optional models.dev provider identifier used to enrich model metadata.
    /// </summary>
    [JsonPropertyName("models_dev_provider_id")]
    public string? ModelsDevProviderId { get; set; }

    /// <summary>
    /// Gets or sets whether this provider is the backend default.
    /// </summary>
    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets the optional compatibility-profile override.
    /// </summary>
    [JsonPropertyName("profile")]
    public CodeAltaRawApiProviderProfileDocument? Profile { get; set; }

    /// <summary>
    /// Gets or sets the optional compaction override.
    /// </summary>
    [JsonPropertyName("compaction")]
    public CodeAltaRawApiCompactionDocument? Compaction { get; set; }

    /// <summary>
    /// Gets or sets optional per-model metadata overrides.
    /// </summary>
    [JsonPropertyName("model_overrides")]
    public Dictionary<string, CodeAltaRawApiModelOverrideDocument>? ModelOverrides { get; set; }
}
