using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The variables that tell the programs a session starts (its shell commands, its background jobs, the CLI of its
/// provider) that they run under an agent of CodeAlta, and which session and model: for scripts and tools that behave
/// differently under an agent, in every application of CodeAlta.
/// </summary>
public static class SessionProcessEnvironment
{
    /// <summary>The variable that holds the identifier of the session.</summary>
    public const string SessionIdVariable = "CODEALTA_SESSION_ID";

    /// <summary>The variable that holds the model of the session, as a model reference <c>provider:model@reasoning</c>.</summary>
    public const string ModelVariable = "CODEALTA_MODEL";

    /// <summary>The variable, shared by coding agents, that names the agent a program runs under.</summary>
    public const string AgentVariable = "AI_AGENT";

    /// <summary>The value of <see cref="AgentVariable"/>.</summary>
    public const string AgentName = "codealta";

    /// <summary>Adds the variables of a session to the environment its processes start with.</summary>
    /// <param name="environment">The environment of the session (the identity of its space), whose other variables are kept.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="providerKey">The key of its provider.</param>
    /// <param name="model">Its model, or <see langword="null"/> when it names none.</param>
    /// <param name="reasoningEffort">Its reasoning effort, or <see langword="null"/>.</param>
    /// <returns>The environment with the variables of the session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="providerKey"/> is blank.</exception>
    public static AgentProcessEnvironment WithMarkers(AgentProcessEnvironment environment, string sessionId, string providerKey, string? model,
        AgentReasoningEffort? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        return new AgentProcessEnvironment([
            .. environment.Variables,
            new(SessionIdVariable, sessionId),
            new(ModelVariable, FormatModel(providerKey, model, reasoningEffort)),
            new(AgentVariable, AgentName),
        ]);
    }

    /// <summary>Writes the model of a session as a model reference: <c>provider</c>, <c>provider:model</c> or <c>provider:model@reasoning</c>.</summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <param name="model">The model, or <see langword="null"/>.</param>
    /// <param name="reasoningEffort">The reasoning effort, or <see langword="null"/>.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="providerKey"/> is blank.</exception>
    public static string FormatModel(string providerKey, string? model, AgentReasoningEffort? reasoningEffort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        var reference = string.IsNullOrWhiteSpace(model) ? providerKey.Trim() : providerKey.Trim() + ":" + model.Trim();
        return reasoningEffort is { } effort ? reference + "@" + CodeAltaConfigStore.FormatReasoningEffort(effort) : reference;
    }
}
