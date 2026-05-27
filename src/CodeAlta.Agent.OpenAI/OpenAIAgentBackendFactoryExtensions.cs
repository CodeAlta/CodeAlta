namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// Extension methods for registering OpenAI-backed raw-API runtimes.
/// </summary>
public static class OpenAIAgentBackendFactoryExtensions
{
    /// <summary>
    /// Registers an OpenAI Responses backend.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">The backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    public static AgentBackendFactory RegisterOpenAIResponses(
        this AgentBackendFactory factory,
        OpenAIResponsesAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.Register(
            options.BackendIdOverride ?? AgentBackendIds.OpenAIResponses,
            () => new OpenAIResponsesAgentBackend(options));
        return factory;
    }

    /// <summary>
    /// Registers an OpenAI Chat/Completions backend.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">The backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    public static AgentBackendFactory RegisterOpenAIChat(
        this AgentBackendFactory factory,
        OpenAIChatAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.Register(
            options.BackendIdOverride ?? AgentBackendIds.OpenAIChat,
            () => new OpenAIChatAgentBackend(options));
        return factory;
    }
}
