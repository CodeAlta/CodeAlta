using System.Text.Json.Serialization;
using Tomlyn.Model;

namespace CodeAlta.Catalog;

/// <summary>
/// Represents a configurable compatibility profile override for a provider.
/// </summary>
public sealed class CodeAltaProviderProfileDocument
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
/// Represents configurable provider compaction settings.
/// </summary>
public sealed class CodeAltaProviderCompactionDocument
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("trigger_threshold")]
    public double? TriggerThreshold { get; set; }

    [JsonPropertyName("target_threshold")]
    public double? TargetThreshold { get; set; }

    [JsonPropertyName("reserved_output_tokens")]
    public int? ReservedOutputTokens { get; set; }

    [JsonPropertyName("reserved_overhead_tokens")]
    public int? ReservedOverheadTokens { get; set; }

    [JsonPropertyName("keep_last_user_message")]
    public bool? KeepLastUserMessage { get; set; }

    [JsonPropertyName("allow_split_turn")]
    public bool? AllowSplitTurn { get; set; }

    [JsonPropertyName("target_context_ratio_ideal")]
    public double? TargetContextRatioIdeal { get; set; }

    [JsonPropertyName("target_context_ratio_max")]
    public double? TargetContextRatioMax { get; set; }

    [JsonPropertyName("recent_suffix_target_tokens")]
    public int? RecentSuffixTargetTokens { get; set; }

    [JsonPropertyName("summary_output_tokens")]
    public int? SummaryOutputTokens { get; set; }

    [JsonPropertyName("summary_input_tokens")]
    public int? SummaryInputTokens { get; set; }

    [JsonPropertyName("tool_result_chars_per_item")]
    public int? ToolResultCharsPerItem { get; set; }

    [JsonPropertyName("tool_result_chars_total")]
    public int? ToolResultCharsTotal { get; set; }

    [JsonPropertyName("reasoning_chars_per_item")]
    public int? ReasoningCharsPerItem { get; set; }

    [JsonPropertyName("reasoning_chars_total")]
    public int? ReasoningCharsTotal { get; set; }

    [JsonPropertyName("reasoning_mode")]
    public string? ReasoningMode { get; set; }

    [JsonPropertyName("max_chunk_passes")]
    public int? MaxChunkPasses { get; set; }

    [JsonPropertyName("allow_oversized_anchor_reduction")]
    public bool? AllowOversizedAnchorReduction { get; set; }

    [JsonPropertyName("prefer_recent_messages")]
    public bool? PreferRecentMessages { get; set; }

    [JsonPropertyName("prefer_recent_tool_outputs")]
    public bool? PreferRecentToolOutputs { get; set; }

    [JsonPropertyName("drop_messages_only_when_summary_input_exceeds_budget")]
    public bool? DropMessagesOnlyWhenSummaryInputExceedsBudget { get; set; }
}

/// <summary>
/// Represents one configurable provider model metadata override.
/// </summary>
public sealed class CodeAltaProviderModelOverrideDocument
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("context_window")]
    public long? ContextWindow { get; set; }

    [JsonPropertyName("input_token_limit")]
    public long? InputTokenLimit { get; set; }

    [JsonPropertyName("output_token_limit")]
    public long? OutputTokenLimit { get; set; }

    [JsonPropertyName("max_tokens")]
    public long? MaxTokens { get; set; }

    [JsonPropertyName("supports_reasoning")]
    public bool? SupportsReasoning { get; set; }

    [JsonPropertyName("supports_tool_call")]
    public bool? SupportsToolCall { get; set; }

    [JsonPropertyName("supports_attachments")]
    public bool? SupportsAttachments { get; set; }

    [JsonPropertyName("supports_structured_output")]
    public bool? SupportsStructuredOutput { get; set; }
}

/// <summary>
/// Represents one configured provider.
/// </summary>
public sealed class CodeAltaProviderDocument
{
    /// <summary>
    /// The default enabled value.
    /// </summary>
    public const bool DefaultEnabled = true;

    /// <summary>
    /// Gets or sets the normalized provider key.
    /// </summary>
    [JsonIgnore]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the provider registration is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets the user-facing provider display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the canonical provider type.
    /// </summary>
    [JsonPropertyName("type")]
    public string? ProviderType { get; set; }

    /// <summary>
    /// Gets or sets the default model for the provider.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the default reasoning effort for the provider.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

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
    /// Gets or sets the API endpoint override.
    /// </summary>
    [JsonPropertyName("api_url")]
    public string? ApiUrl { get; set; }

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
    /// Gets or sets the optional models.dev provider identifier used to enrich model metadata.
    /// </summary>
    [JsonPropertyName("models_dev_provider_id")]
    public string? ModelsDevProviderId { get; set; }

    /// <summary>
    /// Gets or sets the optional fixed model identifier for single-model endpoints.
    /// </summary>
    [JsonPropertyName("single_model_id")]
    public string? SingleModelId { get; set; }

    /// <summary>
    /// Gets or sets provider-specific OpenAI-compatible request-body fields that should be added to outgoing requests.
    /// </summary>
    [JsonPropertyName("extra_body")]
    public TomlTable? ExtraBody { get; set; }

    /// <summary>
    /// Gets or sets the optional compatibility-profile override.
    /// </summary>
    [JsonPropertyName("profile")]
    public CodeAltaProviderProfileDocument? Profile { get; set; }

    /// <summary>
    /// Gets or sets the optional compaction override.
    /// </summary>
    [JsonPropertyName("compaction")]
    public CodeAltaProviderCompactionDocument? Compaction { get; set; }

    /// <summary>
    /// Gets or sets optional per-model metadata overrides.
    /// </summary>
    [JsonPropertyName("model_overrides")]
    public Dictionary<string, CodeAltaProviderModelOverrideDocument>? ModelOverrides { get; set; }
}
