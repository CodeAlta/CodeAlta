namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Base class for CodeAlta plugins.
/// </summary>
/// <remarks>
/// Plugin authors usually inherit from this type, override only the contribution or callback methods they need,
/// and let the runtime register and remove the returned contributions automatically.
/// </remarks>
public abstract class PluginBase : IAsyncDisposable
{
    private PluginRuntimeContext? _context;

    /// <summary>
    /// Gets the runtime context attached by the host before initialization.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected PluginRuntimeContext Context => _context ?? throw new InvalidOperationException("The plugin runtime context has not been attached.");

    /// <summary>
    /// Gets the plugin logger attached by the host.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected XenoAtom.Logging.Logger Logger => Context.Logger;

    /// <summary>
    /// Gets host services attached by the runtime.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected IPluginServices Services => Context.Services;

    /// <summary>
    /// Gets UI services attached by the runtime.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected IPluginUiService Ui => Context.Services.Ui;

    /// <summary>
    /// Gets the runtime-assigned plugin scope.
    /// </summary>
    /// <remarks>
    /// The runtime determines this from the plugin load location. Plugins loaded from <c>~/.alta/plugins</c>
    /// are global; plugins loaded from <c>{project}/.alta/plugins</c> are project-scoped.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected PluginScope Scope => Context.Scope;

    /// <summary>
    /// Gets the scoped project identifier for a project-scoped plugin, when known.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected string? ScopeProjectId => Context.ScopeProjectId;

    /// <summary>
    /// Gets the scoped project path for a project-scoped plugin, when known.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected string? ScopeProjectPath => Context.ScopeProjectPath;

    /// <summary>
    /// Attaches a runtime context to this plugin instance.
    /// </summary>
    /// <param name="context">The runtime context.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public void AttachRuntimeContext(PluginRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfInvalid();
        _context = context;
    }

    /// <summary>
    /// Returns optional descriptor overrides supplied by plugin code.
    /// </summary>
    /// <returns>A descriptor override, or <see langword="null"/> to use metadata/default discovery.</returns>
    public virtual PluginDescriptor? Describe() => null;

    /// <summary>
    /// Initializes plugin-owned state after the runtime context is attached.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel initialization.</param>
    /// <returns>A task representing asynchronous initialization.</returns>
    public virtual ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Notifies the plugin that it is globally active.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel activation work.</param>
    /// <returns>A task representing asynchronous activation.</returns>
    public virtual ValueTask OnActivatedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Notifies the plugin that the runtime is deactivating it.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel cleanup work.</param>
    /// <returns>A task representing asynchronous deactivation.</returns>
    public virtual ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Gets early startup contributions.
    /// </summary>
    /// <returns>Startup contributions.</returns>
    public virtual IEnumerable<PluginStartupContribution> GetStartupContributions() => [];

    /// <summary>
    /// Gets command-line option contributions.
    /// </summary>
    /// <returns>Command-line contributions.</returns>
    public virtual IEnumerable<PluginCommandLineContribution> GetCommandLineContributions() => [];

    /// <summary>
    /// Gets command and shortcut contributions.
    /// </summary>
    /// <returns>Command contributions.</returns>
    public virtual IEnumerable<PluginCommandContribution> GetCommands() => [];

    /// <summary>
    /// Gets agent tool contributions.
    /// </summary>
    /// <returns>Agent tool contributions.</returns>
    public virtual IEnumerable<PluginAgentToolContribution> GetAgentTools() => [];

    /// <summary>
    /// Gets agent backend/provider contributions.
    /// </summary>
    /// <returns>Agent backend contributions.</returns>
    public virtual IEnumerable<PluginAgentBackendContribution> GetAgentBackends() => [];

    /// <summary>
    /// Gets static system/developer prompt contributions.
    /// </summary>
    /// <returns>System prompt contributions.</returns>
    public virtual IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions() => [];

    /// <summary>
    /// Gets prompt processor contributions.
    /// </summary>
    /// <returns>Prompt processor contributions.</returns>
    public virtual IEnumerable<PluginPromptProcessorContribution> GetPromptProcessors() => [];

    /// <summary>
    /// Gets compaction contributions.
    /// </summary>
    /// <returns>Compaction contributions.</returns>
    public virtual IEnumerable<PluginCompactionContribution> GetCompactionContributions() => [];

    /// <summary>
    /// Gets UI contributions.
    /// </summary>
    /// <returns>UI contributions.</returns>
    public virtual IEnumerable<PluginUiContribution> GetUiContributions() => [];

    /// <summary>
    /// Gets resource root contributions.
    /// </summary>
    /// <returns>Resource contributions.</returns>
    public virtual IEnumerable<PluginResourceContribution> GetResources() => [];

    /// <summary>
    /// Observes or transforms prompt submission.
    /// </summary>
    /// <param name="context">The prompt submission context.</param>
    /// <param name="cancellationToken">A token to cancel prompt processing.</param>
    /// <returns>An optional prompt result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask<PluginPromptResult?> OnPromptSubmittingAsync(
        PluginPromptSubmittingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginPromptResult?>((PluginPromptResult?)null);
    }

    /// <summary>
    /// Supplies dynamic context before an agent run starts.
    /// </summary>
    /// <param name="context">The before-run context.</param>
    /// <param name="cancellationToken">A token to cancel processing.</param>
    /// <returns>An optional before-run result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(
        PluginBeforeAgentRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginBeforeAgentRunResult?>((PluginBeforeAgentRunResult?)null);
    }

    /// <summary>
    /// Intercepts an agent tool call before execution.
    /// </summary>
    /// <param name="context">The tool-call context.</param>
    /// <param name="cancellationToken">A token to cancel processing.</param>
    /// <returns>An optional tool-call result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask<PluginToolCallResult?> OnToolCallAsync(
        PluginToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginToolCallResult?>((PluginToolCallResult?)null);
    }

    /// <summary>
    /// Intercepts a tool result before it is returned to the model.
    /// </summary>
    /// <param name="context">The tool-result context.</param>
    /// <param name="cancellationToken">A token to cancel processing.</param>
    /// <returns>An optional tool-result transformation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask<PluginToolResult?> OnToolResultAsync(
        PluginToolResultContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginToolResult?>((PluginToolResult?)null);
    }

    /// <summary>
    /// Observes a normalized agent event.
    /// </summary>
    /// <param name="context">The agent event context.</param>
    /// <param name="cancellationToken">A token to cancel processing.</param>
    /// <returns>A task representing asynchronous observation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask OnAgentEventAsync(
        PluginAgentEventContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disposes plugin-owned resources.
    /// </summary>
    /// <returns>A task representing asynchronous disposal.</returns>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
