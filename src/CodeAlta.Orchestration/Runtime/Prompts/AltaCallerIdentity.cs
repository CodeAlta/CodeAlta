namespace CodeAlta.LiveTool;

/// <summary>
/// Identifies the caller invoking the in-process <c>alta</c> command surface.
/// </summary>
public sealed record AltaCallerIdentity
{
    /// <summary>Gets the caller kind: <c>cli</c>, <c>agent</c>, <c>host</c>, <c>plugin</c>, <c>reminder</c>, or <c>mcp</c> (a client of the MCP server of the desktop application).</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the source CodeAlta session id, when the caller is associated with one.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the source agent id, when known.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the source project id, when known.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets the runtime key of the invoking plugin, when applicable.</summary>
    public string? PluginRuntimeKey { get; init; }

    /// <summary>
    /// Gets the provenance a prompt this caller sends is recorded with (an identifier of
    /// <see cref="CodeAlta.Agent.AgentPromptProvenance"/>, such as <c>user-review</c>), when the host names one; null to
    /// have it follow from <see cref="Kind"/>. Set by the host only, never by CLI arguments.
    /// </summary>
    public string? PromptProvenance { get; init; }

    /// <summary>
    /// Gets a value indicating whether the caller acts for the user, who asked for the command in a client of a plugin (the
    /// phone of the Mobile plugin): the prompts it sends are the user's, with no provenance of its <see cref="Kind"/>. Set
    /// by the host only, never by CLI arguments.
    /// </summary>
    public bool OnBehalfOfUser { get; init; }

    /// <summary>Gets the tools of the calling run, where a command can register tools for the rest of that run; never supplied by CLI arguments.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CodeAlta.Agent.AgentRunTools? RunTools { get; init; }

    /// <summary>
    /// Gets whether the caller may start sessions: false for a session created without sub-sessions
    /// (<see cref="CodeAlta.Agent.AgentSessionOptions.SubSessions"/>), whose commands that start sessions are hidden and
    /// refused. True by default; set by the host only, from the options of the calling session.
    /// </summary>
    public bool SubSessions { get; init; } = true;

    /// <summary>Gets the run of the Code Mode script that makes the call, or null; set by the host only, never by a script or by CLI arguments.</summary>
    public string? ScriptRunId { get; init; }

    /// <summary>Gets the step of the Code Mode script that makes the call, or null; set by the host only.</summary>
    public string? ScriptStepId { get; init; }

    /// <summary>Gets a command-line caller identity.</summary>
    public static AltaCallerIdentity Cli { get; } = new() { Kind = "cli" };

    /// <summary>Gets a host caller identity.</summary>
    public static AltaCallerIdentity Host { get; } = new() { Kind = "host" };
}
