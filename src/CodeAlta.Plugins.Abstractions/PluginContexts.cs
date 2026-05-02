using CodeAlta.Agent;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Identifies where a plugin was loaded from and where its contributions apply by default.
/// </summary>
/// <remarks>
/// The runtime sets this value from the plugin load location. Plugin authors do not choose their own scope.
/// Plugins loaded from the user plugin directory are global; plugins loaded from a project-local plugin
/// directory are project-scoped.
/// </remarks>
public enum PluginScope
{
    /// <summary>
    /// The plugin was loaded from the user-wide plugin location and applies globally.
    /// </summary>
    Global,

    /// <summary>
    /// The plugin was loaded from a project-local plugin location and applies only to that project by default.
    /// </summary>
    Project,
}

/// <summary>
/// Describes the CodeAlta host seen by a plugin.
/// </summary>
public sealed record PluginHostInfo
{
    /// <summary>Gets the application name.</summary>
    public required string ApplicationName { get; init; }

    /// <summary>Gets the application version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the host API version exposed to plugins.</summary>
    public required string HostApiVersion { get; init; }

    /// <summary>Gets the user data directory.</summary>
    public required string UserDataDirectory { get; init; }

    /// <summary>Gets the current working directory, when known.</summary>
    public string? CurrentWorkingDirectory { get; init; }

    /// <summary>Gets a value indicating whether an interactive UI is available.</summary>
    public bool HasInteractiveUi { get; init; }

    /// <summary>Gets a value indicating whether the host is running in headless mode.</summary>
    public bool IsHeadless { get; init; }

    /// <summary>Gets a value indicating whether plugins are in the bootstrap phase.</summary>
    public bool IsBootstrapPhase { get; init; }
}

/// <summary>
/// Runtime context attached to a plugin by the host.
/// </summary>
public sealed class PluginRuntimeContext
{
    private bool _isValid = true;

    /// <summary>Gets the plugin descriptor.</summary>
    public required PluginDescriptor Plugin { get; init; }

    /// <summary>Gets host information.</summary>
    public required PluginHostInfo Host { get; init; }

    /// <summary>Gets the plugin logger.</summary>
    public required Logger Logger { get; init; }

    /// <summary>Gets host services.</summary>
    public required IPluginServices Services { get; init; }

    /// <summary>Gets the plugin package directory.</summary>
    public required string PackageDirectory { get; init; }

    /// <summary>
    /// Gets the runtime-assigned plugin scope.
    /// </summary>
    /// <remarks>
    /// This value is determined by where the runtime loaded the plugin from, such as <c>~/.alta/plugins</c>
    /// for global plugins or <c>{project}/.alta/plugins</c> for project-scoped plugins.
    /// </remarks>
    public PluginScope Scope { get; init; } = PluginScope.Global;

    /// <summary>Gets the project identifier for a project-scoped plugin, when known.</summary>
    public string? ScopeProjectId { get; init; }

    /// <summary>Gets the project path for a project-scoped plugin, when known.</summary>
    public string? ScopeProjectPath { get; init; }

    /// <summary>Gets a lifetime cancellation token cancelled when the plugin is deactivated.</summary>
    public CancellationToken LifetimeCancellationToken { get; init; }

    /// <summary>Gets a value indicating whether this plugin is global.</summary>
    public bool IsGlobalScope => Scope == PluginScope.Global;

    /// <summary>Gets a value indicating whether this plugin is project-scoped.</summary>
    public bool IsProjectScope => Scope == PluginScope.Project;

    /// <summary>Gets a value indicating whether this context is still valid.</summary>
    public bool IsValid => _isValid && !LifetimeCancellationToken.IsCancellationRequested;

    /// <summary>Invalidates the context after deactivation or reload.</summary>
    public void Invalidate() => _isValid = false;

    /// <summary>
    /// Determines whether this plugin scope applies to a project.
    /// </summary>
    /// <param name="projectId">The project identifier to test.</param>
    /// <param name="projectPath">The project path to test.</param>
    /// <returns><see langword="true"/> when a global plugin applies, or when a project-scoped plugin matches the supplied project.</returns>
    public bool AppliesToProject(string? projectId, string? projectPath)
    {
        if (Scope == PluginScope.Global)
        {
            return true;
        }

        return MatchesScopedProject(projectId, projectPath);
    }

    /// <summary>Throws when the context is no longer valid.</summary>
    /// <exception cref="ObjectDisposedException">Thrown when the context is invalid.</exception>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new ObjectDisposedException(nameof(PluginRuntimeContext), "The plugin runtime context is no longer valid.");
        }
    }

    private bool MatchesScopedProject(string? projectId, string? projectPath)
    {
        if (!string.IsNullOrWhiteSpace(ScopeProjectId) &&
            !string.IsNullOrWhiteSpace(projectId) &&
            string.Equals(ScopeProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ScopeProjectPath) &&
            !string.IsNullOrWhiteSpace(projectPath))
        {
            var scopedPath = Path.GetFullPath(ScopeProjectPath);
            var candidatePath = Path.GetFullPath(projectPath);
            return string.Equals(scopedPath, candidatePath, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>
/// Base context for plugin operations.
/// </summary>
public abstract class PluginOperationContext
{
    private bool _isValid = true;

    /// <summary>Gets the plugin descriptor.</summary>
    public required PluginDescriptor Plugin { get; init; }

    /// <summary>Gets host services.</summary>
    public required IPluginServices Services { get; init; }

    /// <summary>Gets the runtime-assigned plugin scope.</summary>
    public PluginScope Scope { get; init; } = PluginScope.Global;

    /// <summary>Gets the project identifier for a project-scoped plugin, when known.</summary>
    public string? ScopeProjectId { get; init; }

    /// <summary>Gets the project path for a project-scoped plugin, when known.</summary>
    public string? ScopeProjectPath { get; init; }

    /// <summary>Gets the project identifier, when known.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the project path, when known.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets the thread identifier, when known.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Gets the run identifier, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets the backend identifier, when known.</summary>
    public string? BackendId { get; init; }

    /// <summary>Gets the active model name, when known.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the operation cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Gets a value indicating whether this context is still valid.</summary>
    public bool IsValid => _isValid && !CancellationToken.IsCancellationRequested;

    /// <summary>Invalidates the context after the operation finishes.</summary>
    public void Invalidate() => _isValid = false;

    /// <summary>
    /// Determines whether this plugin operation applies to its current project.
    /// </summary>
    /// <returns><see langword="true"/> when the plugin is global, or when the project-scoped plugin matches the operation project.</returns>
    public bool AppliesToCurrentProject()
    {
        if (Scope == PluginScope.Global)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ScopeProjectId) &&
            !string.IsNullOrWhiteSpace(ProjectId) &&
            string.Equals(ScopeProjectId, ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ScopeProjectPath) &&
            !string.IsNullOrWhiteSpace(ProjectPath))
        {
            var scopedPath = Path.GetFullPath(ScopeProjectPath);
            var candidatePath = Path.GetFullPath(ProjectPath);
            return string.Equals(scopedPath, candidatePath, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Throws when the context is no longer valid.</summary>
    /// <exception cref="ObjectDisposedException">Thrown when the context is invalid.</exception>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new ObjectDisposedException(GetType().Name, "The plugin operation context is no longer valid.");
        }
    }
}

/// <summary>Context for early startup contributions.</summary>
public sealed class PluginStartupContext : PluginOperationContext
{
    /// <summary>Gets raw startup arguments.</summary>
    public IReadOnlyList<string> RawArguments { get; init; } = [];

    /// <summary>Gets configuration paths known during bootstrap.</summary>
    public IReadOnlyList<string> ConfigurationPaths { get; init; } = [];

    /// <summary>Gets environment values visible to the host.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Context for plugin command-line option handlers.</summary>
public sealed class PluginCommandLineContext : PluginOperationContext
{
    /// <summary>Gets the option name without leading dashes.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the raw option values.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>Gets raw startup arguments.</summary>
    public IReadOnlyList<string> RawArguments { get; init; } = [];
}

/// <summary>Context for plugin command handlers.</summary>
public sealed class PluginCommandContext : PluginOperationContext
{
    /// <summary>Gets UI services.</summary>
    public IPluginUiService Ui => Services.Ui;

    /// <summary>Gets thread services.</summary>
    public IPluginThreadService Threads => Services.Threads;

    /// <summary>Gets prompt services.</summary>
    public IPluginPromptService Prompts => Services.Prompts;

    /// <summary>Gets workspace services.</summary>
    public IPluginWorkspaceService Workspace => Services.Workspace;

    /// <summary>Gets the command invocation arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets the raw invocation text, when available.</summary>
    public string? RawText { get; init; }
}

/// <summary>Context for prompt processor and prompt-submission callbacks.</summary>
public sealed class PluginPromptSubmittingContext : PluginOperationContext
{
    /// <summary>Gets the prompt text being submitted.</summary>
    public required string Text { get; init; }

    /// <summary>Gets prompt attachments.</summary>
    public IReadOnlyList<PluginPromptAttachment> Attachments { get; init; } = [];

    /// <summary>Gets a value indicating whether the target backend is CodeAlta-managed local/raw.</summary>
    public bool IsCodeAltaManagedBackend { get; init; }
}

/// <summary>Context for system prompt content providers.</summary>
public sealed class PluginSystemPromptContext : PluginOperationContext
{
    /// <summary>Gets the prompt channel being built.</summary>
    public PluginPromptChannel Channel { get; init; }

    /// <summary>Gets a value indicating whether the backend supports direct prompt contribution injection.</summary>
    public bool SupportsDirectInjection { get; init; }
}

/// <summary>Context for before-agent-run callbacks.</summary>
public sealed class PluginBeforeAgentRunContext : PluginOperationContext
{
    /// <summary>Gets the user prompt text for the turn, when available.</summary>
    public string? PromptText { get; init; }

    /// <summary>Gets the input sent to the agent backend, when already materialized.</summary>
    public AgentInput? Input { get; init; }

    /// <summary>Gets active tool names.</summary>
    public IReadOnlyList<string> ActiveToolNames { get; init; } = [];
}

/// <summary>Context for plugin tool-call interception.</summary>
public sealed class PluginToolCallContext : PluginOperationContext
{
    /// <summary>Gets the tool invocation.</summary>
    public required AgentToolInvocation Invocation { get; init; }
}

/// <summary>Context for plugin tool-result interception.</summary>
public sealed class PluginToolResultContext : PluginOperationContext
{
    /// <summary>Gets the tool invocation.</summary>
    public required AgentToolInvocation Invocation { get; init; }

    /// <summary>Gets the tool result.</summary>
    public required AgentToolResult Result { get; init; }
}

/// <summary>Context for normalized agent event observation.</summary>
public sealed class PluginAgentEventContext : PluginOperationContext
{
    /// <summary>Gets the normalized agent event.</summary>
    public required AgentEvent Event { get; init; }

    /// <summary>Gets read-only session metadata, when available.</summary>
    public AgentSessionMetadata? Session { get; init; }
}

/// <summary>Context for UI visual factory callbacks.</summary>
public sealed class PluginVisualContext : PluginOperationContext
{
    /// <summary>Gets the UI region being rendered.</summary>
    public required PluginUiRegion Region { get; init; }

    /// <summary>Gets a value indicating whether the host has an interactive UI.</summary>
    public bool HasInteractiveUi { get; init; }
}

/// <summary>Context for status provider callbacks.</summary>
public sealed class PluginStatusContext : PluginOperationContext
{
    /// <summary>Gets the current UTC time supplied by the runtime.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Context for UI renderer callbacks.</summary>
public sealed class PluginRendererContext : PluginOperationContext
{
    /// <summary>Gets the schema or target name being rendered.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the payload to render.</summary>
    public object? Payload { get; init; }
}

/// <summary>Context for agent backend factory callbacks.</summary>
public sealed class PluginAgentBackendFactoryContext : PluginOperationContext
{
    /// <summary>Gets the plugin logger.</summary>
    public required Logger Logger { get; init; }

    /// <summary>Gets the plugin package directory.</summary>
    public required string PackageDirectory { get; init; }

    /// <summary>Gets configuration paths available to the backend.</summary>
    public IReadOnlyList<string> ConfigurationPaths { get; init; } = [];

    /// <summary>Gets environment values visible to the backend.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Context for resolving plugin resources.</summary>
public sealed class PluginResourceContext : PluginOperationContext
{
    /// <summary>Gets the plugin package directory.</summary>
    public required string PackageDirectory { get; init; }
}

/// <summary>Base context for compaction-related callbacks.</summary>
public abstract class PluginCompactionContext : PluginOperationContext
{
    /// <summary>Gets the compaction operation identifier, when known.</summary>
    public string? CompactionId { get; init; }

    /// <summary>Gets metadata about the compaction plan or result.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Context for before-compaction hooks.</summary>
public sealed class PluginBeforeCompactionContext : PluginCompactionContext
{
    /// <summary>Gets a textual summary of the compaction plan, when available.</summary>
    public string? PlanSummary { get; init; }
}

/// <summary>Context for compaction instruction providers.</summary>
public sealed class PluginCompactionInstructionContext : PluginCompactionContext
{
    /// <summary>Gets the maximum preferred instruction length in characters, when known.</summary>
    public int? PreferredMaximumCharacters { get; init; }
}

/// <summary>Context for compaction reducer callbacks.</summary>
public sealed class PluginCompactionReducerContext : PluginCompactionContext
{
    /// <summary>Gets the plugin-owned payload to reduce.</summary>
    public object? Payload { get; init; }

    /// <summary>Gets the payload schema or kind.</summary>
    public string? PayloadKind { get; init; }
}

/// <summary>Context for after-compaction hooks.</summary>
public sealed class PluginAfterCompactionContext : PluginCompactionContext
{
    /// <summary>Gets a value indicating whether compaction succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the resulting summary text, when available.</summary>
    public string? Summary { get; init; }
}
