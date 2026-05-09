using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

/// <summary>
/// Identifies the caller invoking the in-process <c>alta</c> command surface.
/// </summary>
public sealed record AltaCallerIdentity
{
    /// <summary>Gets the caller kind: <c>cli</c>, <c>agent</c>, <c>host</c>, or <c>plugin</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the source CodeAlta thread id, when the caller is associated with one.</summary>
    public string? SourceThreadId { get; init; }

    /// <summary>Gets the source backend session id, when known.</summary>
    public string? SourceBackendSessionId { get; init; }

    /// <summary>Gets the source agent/session id, when known.</summary>
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

/// <summary>
/// Classifies an <c>alta</c> command for policy, audit, and capability discovery.
/// </summary>
public sealed record AltaCommandPolicy
{
    /// <summary>Gets the command path without the executable name, for example <c>session send</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Gets a value indicating whether the command requires the running in-process host.</summary>
    public bool RequiresInProcessRuntime { get; init; }

    /// <summary>Gets a value indicating whether the command mutates runtime or catalog state.</summary>
    public bool IsMutating { get; init; }

    /// <summary>Gets a value indicating whether the command is disruptive.</summary>
    public bool IsDisruptive { get; init; }

    /// <summary>Gets a value indicating whether the command can run with catalog-only services.</summary>
    public bool SupportsCatalogOnlyContext { get; init; }
}

/// <summary>
/// Per-invocation context for <c>alta</c> command handlers.
/// </summary>
public sealed record AltaCommandContext
{
    /// <summary>Gets the caller identity.</summary>
    public required AltaCallerIdentity Caller { get; init; }

    /// <summary>Gets host services available to command handlers.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Gets the command stdin stream.</summary>
    public required TextReader Stdin { get; init; }

    /// <summary>Gets the command stdout writer for JSONL result records.</summary>
    public required TextWriter Stdout { get; init; }

    /// <summary>Gets the command stderr writer for JSONL diagnostic records.</summary>
    public required TextWriter Stderr { get; init; }

    /// <summary>Gets the current working directory supplied by the caller, when any.</summary>
    public string? Cwd { get; init; }

    /// <summary>Gets the correlation id shared by result and diagnostic records.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Gets the maximum number of command records to return, when set.</summary>
    public int? MaxOutputRecords { get; init; }

    /// <summary>Gets the maximum transcript byte count to return, when set.</summary>
    public int? MaxOutputBytes { get; init; }

    /// <summary>Gets the cancellation token for bounded current work.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Context passed while building a fresh command tree for one invocation.
/// </summary>
public sealed record AltaCommandContributionContext
{
    /// <summary>Gets the invocation context captured by command actions.</summary>
    public required AltaCommandContext Invocation { get; init; }
}

/// <summary>
/// Contributes fresh command nodes to the <c>alta</c> command registry.
/// </summary>
public interface IAltaCommandContributor
{
    /// <summary>Creates fresh command-line nodes for a new command tree.</summary>
    /// <param name="context">The contribution context.</param>
    /// <returns>Fresh command nodes.</returns>
    IEnumerable<CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context);

    /// <summary>Gets policy metadata for contributed command paths.</summary>
    /// <param name="context">The contribution context.</param>
    /// <returns>Command policy entries.</returns>
    IEnumerable<AltaCommandPolicy> GetCommandPolicies(AltaCommandContributionContext context);
}

/// <summary>
/// Result returned by an in-process <c>alta</c> dispatch.
/// </summary>
public sealed record AltaCommandResult
{
    /// <summary>Gets the process-style exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Gets captured stdout or the flat live-tool transcript after formatting.</summary>
    public required string Stdout { get; init; }

    /// <summary>Gets captured stderr.</summary>
    public required string Stderr { get; init; }

    /// <summary>Gets a model-visible text result. This is stdout for help and the flat transcript for live-tool calls.</summary>
    public string Transcript => Stdout;

    /// <summary>Gets a value indicating whether this result is help text rather than JSONL.</summary>
    public bool IsHelp { get; init; }

    /// <summary>Gets a value indicating whether output was truncated by dispatcher limits.</summary>
    public bool Truncated { get; init; }

    /// <summary>Gets the command correlation id.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Gets the requested maximum output record count, when any.</summary>
    public int? MaxOutputRecords { get; init; }

    /// <summary>Gets the requested maximum output byte count, when any.</summary>
    public int? MaxOutputBytes { get; init; }

    /// <summary>Gets a short error summary, when the command failed.</summary>
    public string? Error { get; init; }
}
