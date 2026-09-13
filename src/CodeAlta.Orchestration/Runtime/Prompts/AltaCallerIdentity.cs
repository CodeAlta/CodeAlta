namespace CodeAlta.LiveTool;

/// <summary>
/// Identifies the caller invoking the in-process <c>alta</c> command surface.
/// </summary>
public sealed record AltaCallerIdentity
{
    /// <summary>Gets the caller kind: <c>cli</c>, <c>agent</c>, <c>host</c>, or <c>plugin</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the source CodeAlta session id, when the caller is associated with one.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the source agent id, when known.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the source project id, when known.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets the runtime key of the invoking plugin, when applicable.</summary>
    public string? PluginRuntimeKey { get; init; }

    /// <summary>Gets a command-line caller identity.</summary>
    public static AltaCallerIdentity Cli { get; } = new() { Kind = "cli" };

    /// <summary>Gets a host caller identity.</summary>
    public static AltaCallerIdentity Host { get; } = new() { Kind = "host" };
}
