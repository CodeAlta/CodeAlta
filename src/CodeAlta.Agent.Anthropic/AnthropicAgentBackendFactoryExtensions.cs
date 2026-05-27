namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Extension methods for registering Anthropic-backed raw-API runtimes.
/// </summary>
public static class AnthropicAgentBackendFactoryExtensions
{
    /// <summary>
    /// Registers an Anthropic backend.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">The backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    public static AgentBackendFactory RegisterAnthropic(
        this AgentBackendFactory factory,
        AnthropicAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.Register(
            options.BackendIdOverride ?? AgentBackendIds.AnthropicMessages,
            () => new AnthropicAgentBackend(options));
        return factory;
    }
}
