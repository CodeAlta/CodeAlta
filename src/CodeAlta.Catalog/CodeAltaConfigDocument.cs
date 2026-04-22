using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Represents the top-level CodeAlta TOML configuration document.
/// </summary>
public sealed class CodeAltaConfigDocument
{
    /// <summary>
    /// Gets or sets chat-level defaults and preferences.
    /// </summary>
    [JsonPropertyName("chat")]
    public CodeAltaChatSettingsDocument? Chat { get; set; }

    /// <summary>
    /// Gets or sets ACP backend definitions keyed by agent id.
    /// </summary>
    [JsonPropertyName("acp")]
    public CodeAltaAcpSettingsDocument? Acp { get; set; }

    /// <summary>
    /// Gets or sets configured provider definitions keyed by provider key.
    /// </summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, CodeAltaProviderDocument>? Providers { get; set; }
}

/// <summary>
/// Represents chat-specific configuration settings.
/// </summary>
public sealed class CodeAltaChatSettingsDocument
{
    /// <summary>
    /// Gets or sets the default provider key.
    /// </summary>
    [JsonPropertyName("default_provider")]
    public string? DefaultProvider { get; set; }
}

/// <summary>
/// Represents ACP-specific configuration settings.
/// </summary>
public sealed class CodeAltaAcpSettingsDocument
{
    /// <summary>
    /// Gets or sets configured ACP agent backends keyed by agent id.
    /// </summary>
    [JsonPropertyName("agents")]
    public Dictionary<string, AcpBackendDefinition>? Agents { get; set; }
}
