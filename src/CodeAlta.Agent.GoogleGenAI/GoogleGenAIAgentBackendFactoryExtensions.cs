namespace CodeAlta.Agent.GoogleGenAI;

/// <summary>
/// Extension methods for registering Google GenAI-backed raw-API runtimes.
/// </summary>
public static class GoogleGenAIAgentBackendFactoryExtensions
{
    /// <summary>
    /// Registers a Google GenAI backend.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">The backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    public static AgentBackendFactory RegisterGoogleGenAI(
        this AgentBackendFactory factory,
        GoogleGenAIAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.Register(
            options.BackendIdOverride ?? AgentBackendIds.GoogleGenAI,
            () => new GoogleGenAIAgentBackend(options));
        return factory;
    }
}
