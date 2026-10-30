using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// The options a session is created with beside its agent prompt, model, reasoning effort, permission mode and sandbox:
/// what its agent may do and the size of its context, recorded with the session and kept when it is resumed, forked or
/// restarted. A session recorded without options, or before they existed, has the default of each one.
/// </summary>
/// <remarks>
/// An option of what an agent may do narrows it. A session created by another session never has such an option its
/// creator does not have (<see cref="Within"/>): an agent turns an option off for a session it creates, never on.
/// </remarks>
public sealed record AgentSessionOptions
{
    /// <summary>Gets the options of a session that chose none: every option on.</summary>
    public static AgentSessionOptions Default { get; } = new();

    /// <summary>
    /// Gets whether the session can start sessions of its own (sub-agents, sub-sessions): <c>alta session create</c>,
    /// the forks of <c>alta session fork</c>, and the <c>agent()</c> and <c>session()</c> of Code Mode scripts. When it
    /// is off, those tools are not offered and refuse, and the instructions of the session do not mention them.
    /// </summary>
    [JsonPropertyName("sub_sessions")]
    public bool SubSessions { get; init; } = true;

    /// <summary>
    /// Gets the size of context of the session, in input tokens: one of the sizes of its model
    /// (<see cref="AgentModelInfo.ContextSizes"/>). Null, the default, for the size its provider is configured with, else
    /// the size of the model; a size the model does not have is not used.
    /// </summary>
    [JsonPropertyName("context_size")]
    public long? ContextSize { get; init; }

    /// <summary>Gets whether every option is at its default.</summary>
    [JsonIgnore]
    public bool IsDefault => Equals(Default);

    /// <summary>Gets the options a session records: null when they are the defaults, so that a session without a choice records nothing.</summary>
    /// <param name="options">The options, or null for the defaults.</param>
    /// <returns>The options, or null.</returns>
    public static AgentSessionOptions? Stored(AgentSessionOptions? options) => options is null || options.IsDefault ? null : options;

    /// <summary>Gets the options a session has: those it records, or the defaults when it records none.</summary>
    /// <param name="stored">The options the session records, or null.</param>
    /// <returns>The options.</returns>
    public static AgentSessionOptions Of(AgentSessionOptions? stored) => stored ?? Default;

    /// <summary>
    /// Gets these options as a session created by another session has them: an option is on only when it is on here and
    /// in the session that creates it. An agent turns an option off for a session it creates, never on.
    /// </summary>
    /// <param name="creator">The options of the session that creates it, or null when no session does (the user, a host).</param>
    /// <returns>The options of the new session.</returns>
    public AgentSessionOptions Within(AgentSessionOptions? creator)
        => creator is null ? this : this with { SubSessions = SubSessions && creator.SubSessions };
}
