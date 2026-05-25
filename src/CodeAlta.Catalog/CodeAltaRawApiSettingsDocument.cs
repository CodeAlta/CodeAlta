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
    /// Gets or sets whether tool result messages require a tool name field.
    /// </summary>
    [JsonPropertyName("requires_tool_result_name")]
    public bool? RequiresToolResultName { get; set; }

    /// <summary>
    /// Gets or sets whether tool results require a synthetic assistant turn before the next user turn.
    /// </summary>
    [JsonPropertyName("requires_assistant_after_tool_result")]
    public bool? RequiresAssistantAfterToolResult { get; set; }

    /// <summary>
    /// Gets or sets whether the provider supports cache-control metadata.
    /// </summary>
    [JsonPropertyName("supports_cache_control")]
    public bool? SupportsCacheControl { get; set; }

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

    /// <summary>
    /// Gets or sets the provider-specific assistant-message field used to replay reasoning content.
    /// </summary>
    [JsonPropertyName("reasoning_input_field_name")]
    public string? ReasoningInputFieldName { get; set; }
}

/// <summary>
/// Represents configurable provider compaction settings.
/// </summary>
public sealed class CodeAltaProviderCompactionDocument
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("ratio")]
    public double? Ratio { get; set; }

    [JsonPropertyName("summary_output_ratio")]
    public double? SummaryOutputRatio { get; set; }

    [JsonPropertyName("post_compaction_target_ratio")]
    public double? PostCompactionTargetRatio { get; set; }

    [JsonPropertyName("summary_share_of_target")]
    public double? SummaryShareOfTarget { get; set; }

    [JsonPropertyName("file_context_share_of_summary_target")]
    public double? FileContextShareOfSummaryTarget { get; set; }

    [JsonPropertyName("keep_last_user_message")]
    public bool? KeepLastUserMessage { get; set; }

    [JsonPropertyName("allow_split_turn")]
    public bool? AllowSplitTurn { get; set; }
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
/// Represents request-level provider customization.
/// </summary>
public sealed class CodeAltaProviderRequestDocument
{
    /// <summary>
    /// Gets or sets static HTTP headers added to provider requests.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets default/content headers to remove before user headers are applied.
    /// Required authentication headers cannot be removed.
    /// </summary>
    [JsonPropertyName("remove_headers")]
    public List<string>? RemoveHeaders { get; set; }

    /// <summary>
    /// Gets or sets provider-specific OpenAI-compatible request-body fields.
    /// Existing top-level <c>extra_body</c> values take precedence over these values.
    /// </summary>
    [JsonPropertyName("extra_body")]
    public TomlTable? ExtraBody { get; set; }

    /// <summary>
    /// Gets or sets default/content OpenAI-compatible request-body fields to remove before user fields are applied.
    /// </summary>
    [JsonPropertyName("remove_extra_body")]
    public List<string>? RemoveExtraBody { get; set; }
}

/// <summary>
/// Represents one configured provider model request override.
/// </summary>
public sealed class CodeAltaProviderModelRequestDocument
{
    /// <summary>
    /// Gets or sets static HTTP headers added to requests for this model.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets default/provider headers to remove before this model's headers are applied.
    /// Required authentication headers cannot be removed.
    /// </summary>
    [JsonPropertyName("remove_headers")]
    public List<string>? RemoveHeaders { get; set; }

    /// <summary>
    /// Gets or sets OpenAI-compatible request-body fields added to requests for this model.
    /// </summary>
    [JsonPropertyName("extra_body")]
    public TomlTable? ExtraBody { get; set; }

    /// <summary>
    /// Gets or sets provider/default request-body fields to remove before this model's fields are applied.
    /// </summary>
    [JsonPropertyName("remove_extra_body")]
    public List<string>? RemoveExtraBody { get; set; }
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
    /// Gets or sets the GitHub Enterprise URL or domain for the GitHub Copilot direct provider.
    /// </summary>
    [JsonPropertyName("github_enterprise_url")]
    public string? GitHubEnterpriseUrl { get; set; }

    /// <summary>
    /// Gets or sets the environment variable containing a GitHub OAuth token for the GitHub Copilot direct provider.
    /// </summary>
    [JsonPropertyName("github_token_env")]
    public string? GitHubTokenEnv { get; set; }

    /// <summary>
    /// Gets or sets the environment variable containing a pre-exchanged Copilot API token for the GitHub Copilot direct provider.
    /// </summary>
    [JsonPropertyName("copilot_token_env")]
    public string? CopilotTokenEnv { get; set; }

    /// <summary>
    /// Gets or sets whether Copilot model policies may be enabled on demand by the GitHub Copilot direct provider.
    /// </summary>
    [JsonPropertyName("enable_model_policies")]
    public bool? EnableModelPolicies { get; set; }

    /// <summary>
    /// Gets or sets whether preview Copilot models should be included by the GitHub Copilot direct provider.
    /// </summary>
    [JsonPropertyName("include_preview_models")]
    public bool? IncludePreviewModels { get; set; }

    /// <summary>
    /// Gets or sets whether low-level provider protocol tracing is enabled.
    /// </summary>
    [JsonPropertyName("protocol_trace")]
    public bool? ProtocolTrace { get; set; }

    /// <summary>
    /// Gets or sets the ChatGPT/Codex OAuth credential source for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("auth_source")]
    public string? AuthSource { get; set; }

    /// <summary>
    /// Gets or sets the explicit ChatGPT account or workspace identifier for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    /// <summary>
    /// Gets or sets the maximum concurrent requests per ChatGPT account for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("max_concurrent_requests")]
    public int? MaxConcurrentRequests { get; set; }

    /// <summary>
    /// Gets or sets the configured text verbosity for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("text_verbosity")]
    public string? TextVerbosity { get; set; }

    /// <summary>
    /// Gets or sets whether encrypted reasoning continuity should be requested for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("include_encrypted_reasoning")]
    public bool? IncludeEncryptedReasoning { get; set; }

    /// <summary>
    /// Gets or sets the model discovery mode for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("model_discovery")]
    public string? ModelDiscovery { get; set; }

    /// <summary>
    /// Gets or sets the Responses transport mode for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("response_transport")]
    public string? ResponseTransport { get; set; }

    /// <summary>
    /// Gets or sets whether to send the Responses experimental beta header for the Codex subscription provider.
    /// </summary>
    [JsonPropertyName("send_responses_beta_header")]
    public bool? SendResponsesBetaHeader { get; set; }

    /// <summary>
    /// Gets or sets whether to include a stable installation id in Codex subscription request metadata.
    /// </summary>
    [JsonPropertyName("send_installation_id")]
    public bool? SendInstallationId { get; set; }

    /// <summary>
    /// Gets or sets the installation id source used when installation metadata is enabled.
    /// </summary>
    [JsonPropertyName("installation_id_source")]
    public string? InstallationIdSource { get; set; }

    /// <summary>
    /// Gets or sets whether the experimental Codex subscription provider is explicitly enabled.
    /// </summary>
    [JsonPropertyName("experimental")]
    public bool? Experimental { get; set; }

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
    /// Gets or sets optional request-level customizations.
    /// </summary>
    [JsonPropertyName("request")]
    public CodeAltaProviderRequestDocument? Request { get; set; }

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

    /// <summary>
    /// Gets or sets optional per-model request customizations.
    /// </summary>
    [JsonPropertyName("model_request")]
    public Dictionary<string, CodeAltaProviderModelRequestDocument>? ModelRequest { get; set; }
}
