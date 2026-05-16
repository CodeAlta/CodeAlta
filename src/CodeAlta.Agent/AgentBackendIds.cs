namespace CodeAlta.Agent;

/// <summary>
/// Well-known backend identifiers.
/// </summary>
public static class AgentBackendIds
{
    /// <summary>
    /// GitHub Copilot CLI runtime backend.
    /// </summary>
    public static readonly AgentBackendId Copilot = new("copilot_cli");

    /// <summary>
    /// Codex app-server runtime backend.
    /// </summary>
    public static readonly AgentBackendId Codex = new("codex_cli");

    /// <summary>
    /// OpenAI-compatible chat/completions runtime backend.
    /// </summary>
    public static readonly AgentBackendId OpenAIChat = new("openai-chat");

    /// <summary>
    /// OpenAI Responses runtime backend.
    /// </summary>
    public static readonly AgentBackendId OpenAIResponses = new("openai-responses");

    /// <summary>
    /// Anthropic Messages runtime backend.
    /// </summary>
    public static readonly AgentBackendId AnthropicMessages = new("anthropic-messages");

    /// <summary>
    /// Google GenAI runtime backend.
    /// </summary>
    public static readonly AgentBackendId GoogleGenAI = new("google-genai");
}

