using XenoAtom.CommandLine;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Creates a fresh command node for one plugin-contributed <c>alta</c> command tree build.
/// </summary>
/// <param name="context">The per-invocation command contribution context.</param>
/// <returns>A fresh, unattached command node.</returns>
public delegate CommandNode PluginAltaCommandNodeFactory(PluginAltaCommandContext context);

/// <summary>
/// Describes a plugin-contributed <c>alta</c> command.
/// </summary>
public sealed record PluginAltaCommandContribution
{
    /// <summary>Gets the declared command path, such as <c>statistics session summarize</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Gets a concise description used for diagnostics and capability listings.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the command policy classification.</summary>
    public PluginAltaCommandPolicy Policy { get; init; } = new();

    /// <summary>Gets a factory that creates a fresh command node for each registry build.</summary>
    public required PluginAltaCommandNodeFactory CreateCommandNode { get; init; }

    /// <summary>Gets the ordering hint among plugin alta command contributions.</summary>
    public int Order { get; init; }
}

/// <summary>
/// Classifies a plugin-contributed <c>alta</c> command for host policy and capability reporting.
/// </summary>
public sealed record PluginAltaCommandPolicy
{
    /// <summary>Gets a value indicating whether the command requires in-process runtime services.</summary>
    public bool RequiresInProcessRuntime { get; init; } = true;

    /// <summary>Gets a value indicating whether the command mutates CodeAlta or plugin state.</summary>
    public bool IsMutating { get; init; }

    /// <summary>Gets a value indicating whether the command can disrupt active work.</summary>
    public bool IsDisruptive { get; init; }

    /// <summary>Gets a value indicating whether the command can run with catalog-only services.</summary>
    public bool SupportsCatalogOnlyContext { get; init; }
}

/// <summary>
/// Carries per-invocation state supplied to plugin <c>alta</c> command-node factories.
/// </summary>
public sealed record PluginAltaCommandContext
{
    /// <summary>Gets the plugin descriptor that owns the contribution.</summary>
    public required PluginDescriptor Plugin { get; init; }

    /// <summary>Gets host services for the owning plugin.</summary>
    public required IPluginServices Services { get; init; }

    /// <summary>Gets the runtime-assigned plugin scope.</summary>
    public PluginScope Scope { get; init; }

    /// <summary>Gets the scoped project identifier for project plugins, when known.</summary>
    public string? ScopeProjectId { get; init; }

    /// <summary>Gets the scoped project path for project plugins, when known.</summary>
    public string? ScopeProjectPath { get; init; }

    /// <summary>Gets the command correlation identifier.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Gets the invocation working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the source CodeAlta session id associated with the command caller, when known.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the source project id associated with the command caller, when known.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets the source agent id associated with the command caller, when known.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the command standard input reader.</summary>
    public required TextReader Stdin { get; init; }

    /// <summary>Gets the command standard output writer.</summary>
    public required TextWriter Stdout { get; init; }

    /// <summary>Gets the command diagnostic output writer.</summary>
    public required TextWriter Stderr { get; init; }

    /// <summary>Gets the invocation cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Exposes the in-process <c>alta</c> dispatcher to plugins.
/// </summary>
public interface IPluginAltaService
{
    /// <summary>
    /// Invokes an <c>alta</c> command in-process and returns the flat JSONL transcript.
    /// </summary>
    /// <param name="args">Command arguments excluding the <c>alta</c> executable name.</param>
    /// <param name="stdin">Optional standard input text.</param>
    /// <param name="options">Invocation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command result.</returns>
    ValueTask<PluginAltaCommandResult> InvokeAsync(
        IReadOnlyList<string> args,
        string? stdin = null,
        PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-owned extension of <see cref="IPluginAltaService"/> that supplies trusted plugin identity.
/// </summary>
public interface IPluginAltaRuntimeService : IPluginAltaService
{
    /// <summary>
    /// Invokes an <c>alta</c> command for a specific runtime-owned plugin key.
    /// </summary>
    /// <param name="pluginRuntimeKey">The runtime-owned plugin key.</param>
    /// <param name="args">Command arguments excluding the <c>alta</c> executable name.</param>
    /// <param name="stdin">Optional standard input text.</param>
    /// <param name="options">Invocation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command result.</returns>
    ValueTask<PluginAltaCommandResult> InvokeAsync(
        string pluginRuntimeKey,
        IReadOnlyList<string> args,
        string? stdin = null,
        PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes the result of a plugin <c>alta</c> invocation.
/// </summary>
public sealed record PluginAltaCommandResult
{
    /// <summary>Gets the stable alta exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Gets the flat JSONL transcript returned to the plugin.</summary>
    public required string TranscriptJsonl { get; init; }

    /// <summary>Gets a value indicating whether output was truncated.</summary>
    public bool Truncated { get; init; }

    /// <summary>Gets a short error summary, when the command failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Provides optional plugin <c>alta</c> invocation metadata.
/// </summary>
public sealed record PluginAltaInvocationOptions
{
    /// <summary>Gets the source session id associated with the plugin operation.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the source project id associated with the plugin operation.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets the source agent id associated with the plugin operation.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the working directory for the command invocation.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the maximum number of output records to return.</summary>
    public int? MaxOutputRecords { get; init; }

    /// <summary>Gets the maximum output bytes to return.</summary>
    public int? MaxOutputBytes { get; init; }

    /// <summary>Gets the command timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}
