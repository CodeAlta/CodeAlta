namespace CodeNoesis.Agent.Codex;

/// <summary>
/// Extension methods for registering Codex backends in <see cref="AgentBackendFactory"/>.
/// </summary>
public static class CodexAgentBackendFactoryExtensions
{
    /// <summary>
    /// Registers a Codex backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">Codex backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the Codex backend identifier is already registered.
    /// </exception>
    public static AgentBackendFactory RegisterCodex(
        this AgentBackendFactory factory,
        CodexAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.Register(AgentBackendIds.Codex, () => new CodexAgentBackend(options));
        return factory;
    }

    /// <summary>
    /// Registers a Codex backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="backendFactory">Factory delegate that creates a Codex backend.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="backendFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the Codex backend identifier is already registered.
    /// </exception>
    public static AgentBackendFactory RegisterCodex(
        this AgentBackendFactory factory,
        Func<CodexAgentBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(backendFactory);

        factory.Register(AgentBackendIds.Codex, () => backendFactory());
        return factory;
    }

    /// <summary>
    /// Registers or replaces a Codex backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="options">Codex backend options.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public static AgentBackendFactory RegisterOrReplaceCodex(
        this AgentBackendFactory factory,
        CodexAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        factory.RegisterOrReplace(AgentBackendIds.Codex, () => new CodexAgentBackend(options));
        return factory;
    }

    /// <summary>
    /// Registers or replaces a Codex backend factory.
    /// </summary>
    /// <param name="factory">The backend factory.</param>
    /// <param name="backendFactory">Factory delegate that creates a Codex backend.</param>
    /// <returns><paramref name="factory"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="factory"/> or <paramref name="backendFactory"/> is <see langword="null"/>.
    /// </exception>
    public static AgentBackendFactory RegisterOrReplaceCodex(
        this AgentBackendFactory factory,
        Func<CodexAgentBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(backendFactory);

        factory.RegisterOrReplace(AgentBackendIds.Codex, () => backendFactory());
        return factory;
    }
}
