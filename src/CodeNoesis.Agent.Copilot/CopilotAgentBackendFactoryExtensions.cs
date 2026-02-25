namespace CodeNoesis.Agent.Copilot;

/// <summary>
/// Extension methods for registering Copilot backends in <see cref="AgentBackendFactory"/>.
/// </summary>
public static class CopilotAgentBackendFactoryExtensions
{
    /// <summary>
    /// Registers a Copilot backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">Copilot backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the Copilot backend identifier is already registered.
    /// </exception>
    public static AgentBackendFactory RegisterCopilot(
        this AgentBackendFactory factory,
        CopilotAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.Register(AgentBackendIds.Copilot, () => new CopilotAgentBackend(options));
        return factory;
    }

    /// <summary>
    /// Registers a Copilot backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="backendFactory">Factory delegate that creates a Copilot backend.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="backendFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the Copilot backend identifier is already registered.
    /// </exception>
    public static AgentBackendFactory RegisterCopilot(
        this AgentBackendFactory factory,
        Func<CopilotAgentBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(backendFactory);

        factory.Register(AgentBackendIds.Copilot, () => backendFactory());
        return factory;
    }

    /// <summary>
    /// Registers or replaces a Copilot backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">Copilot backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public static AgentBackendFactory RegisterOrReplaceCopilot(
        this AgentBackendFactory factory,
        CopilotAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.RegisterOrReplace(AgentBackendIds.Copilot, () => new CopilotAgentBackend(options));
        return factory;
    }

    /// <summary>
    /// Registers or replaces a Copilot backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="backendFactory">Factory delegate that creates a Copilot backend.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="backendFactory"/> is <see langword="null"/>.
    /// </exception>
    public static AgentBackendFactory RegisterOrReplaceCopilot(
        this AgentBackendFactory factory,
        Func<CopilotAgentBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(backendFactory);

        factory.RegisterOrReplace(AgentBackendIds.Copilot, () => backendFactory());
        return factory;
    }
}
