using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// The options a session is created with beside its agent prompt, model, reasoning effort, permission mode and sandbox:
/// what its agent may do, recorded with the session and kept when it is resumed, forked or restarted. Each option is on
/// by default, so that a session recorded without options, or before they existed, has them all.
/// </summary>
/// <remarks>
/// An option narrows what an agent may do. A session created by another session never has an option its creator does
/// not have (<see cref="Within"/>): an agent turns an option off for a session it creates, never on.
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
        => creator is null ? this : new AgentSessionOptions { SubSessions = SubSessions && creator.SubSessions };
}
