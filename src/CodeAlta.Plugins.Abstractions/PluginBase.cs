using XenoAtom.CommandLine;

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
    /// Gets plugin-lifetime task services attached by the runtime.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the plugin has not been attached to a runtime context.</exception>
    protected IPluginTaskService Tasks => Context.Services.Tasks;

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
    /// Gets command-line contributions.
    /// </summary>
    /// <returns>Command-line contributions.</returns>
    public virtual IEnumerable<CommandNode> GetCommandLineContributions() => [];

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
    /// Gets <c>alta</c> live command contributions.
    /// </summary>
    /// <returns>Alta command contributions.</returns>
    public virtual IEnumerable<PluginAltaCommandContribution> GetAltaCommands() => [];

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
    /// Gets final system/developer instruction processor contributions.
    /// </summary>
    /// <returns>Instruction processor contributions.</returns>
    public virtual IEnumerable<PluginInstructionProcessorContribution> GetInstructionProcessors() => [];

    /// <summary>
    /// Gets plugin-owned prompt editor attachments.
    /// </summary>
    /// <returns>Prompt editor attachment contributions.</returns>
    public virtual IEnumerable<PluginPromptEditorContribution> GetPromptEditorContributions() => [];

    /// <summary>
    /// Gets prompt pickers: lists opened by a trigger character typed in the prompt.
    /// </summary>
    /// <returns>Prompt picker contributions.</returns>
    public virtual IEnumerable<PluginPromptPickerContribution> GetPromptPickers() => [];

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
    /// Gets transient session event projection contributions.
    /// </summary>
    /// <returns>Session event projection contributions.</returns>
    public virtual IEnumerable<PluginSessionEventProjectionContribution> GetSessionEventProjections() => [];

    /// <summary>
    /// Gets resource root contributions.
    /// </summary>
    /// <returns>Resource contributions.</returns>
    public virtual IEnumerable<PluginResourceContribution> GetResources() => [];

    /// <summary>
    /// Gets the canvases of the plugin: tabs that it provides, which the plugin fills and keeps up to date.
    /// </summary>
    /// <returns>Canvas contributions.</returns>
    public virtual IEnumerable<PluginCanvasContribution> GetCanvases() => [];

    /// <summary>
    /// Gets the cards that the plugin pins on the landing page of the desktop application.
    /// </summary>
    /// <returns>Landing card contributions.</returns>
    public virtual IEnumerable<PluginLandingCardContribution> GetLandingCards() => [];

    /// <summary>
    /// Gets the model provider types of the plugin: the providers of the configuration with such a <c>type</c> are
    /// registered through it while it runs, and shown like any other provider.
    /// </summary>
    /// <returns>Model provider contributions.</returns>
    public virtual IEnumerable<PluginModelProviderContribution> GetModelProviders() => [];

    /// <summary>
    /// Gets the settings of the plugin: values the user changes in Settings, which the search of Settings, the search of
    /// the window, <c>alta settings</c> and <c>alta search</c> find. The host keeps them in <c>config.toml</c> under
    /// <c>[plugins.&lt;id&gt;]</c>; the plugin reads them with <see cref="IPluginServices.Settings"/>.
    /// </summary>
    /// <returns>Setting contributions, made with the factories of <see cref="Setting"/>.</returns>
    public virtual IEnumerable<PluginSettingContribution> GetSettings() => [];

    /// <summary>
    /// A hook that observes or transforms a prompt when it is submitted.
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
    /// A hook called before an agent run starts: it adds tools, messages or prompt text for the run, or cancels it.
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
    /// A hook called before every tool call of a session runs: the built-in tools of CodeAlta (<c>shell_command</c>,
    /// <c>write_file</c>...), the tools of plugins and MCP servers, the calls the scripts of Code Mode make, and, where the
    /// provider allows it, the tools an agent CLI runs itself (<see cref="PluginToolCallContext.ToolKind"/> says which).
    /// A plugin that only cares about some tools filters by <see cref="PluginToolCallContext.ToolName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hooks of the plugins run one after the other, before the guards of CodeAlta, the permission prompt and the
    /// sandbox: arguments a hook replaces are those the next hook sees, and they still go through all of these, so a hook
    /// can make the policy stricter, never looser. The first hook that blocks the call ends it: the model reads
    /// <c>Blocked by &lt;plugin&gt;: &lt;reason&gt;</c> as the failed result of the call, and the tool does not run.
    /// <see cref="PluginToolCallContext.Capabilities"/> says what applies to the call (an agent CLI's own tools can be
    /// blocked, not changed).
    /// </para>
    /// <para>
    /// A hook that does not answer within <see cref="HookTimeout"/>, or that throws, is logged in the diagnostics of the
    /// plugin and the call goes on, unless the plugin fails closed (<see cref="ToolHooksFailClosed"/>). A plugin that does
    /// not override this method is not called.
    /// </para>
    /// </remarks>
    /// <param name="context">The tool-call context.</param>
    /// <param name="cancellationToken">Cancelled when the call is cancelled or the hook times out.</param>
    /// <returns><see langword="null"/> or <see cref="PluginToolCallResult.Allow"/> to let the call run; a result made by
    /// <see cref="PluginToolCallResult.Block"/> or <see cref="PluginToolCallResult.ReplaceArguments"/> otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask<PluginToolCallResult?> OnToolCallAsync(
        PluginToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginToolCallResult?>((PluginToolCallResult?)null);
    }

    /// <summary>
    /// A hook called after every tool call a session runs itself (the calls <see cref="OnToolCallAsync"/> sees, except the
    /// tools an agent CLI runs itself), with the result of the tool, before the model is given it.
    /// </summary>
    /// <remarks>
    /// The hooks of the plugins run one after the other, each with the result the hooks before it left
    /// (<see cref="PluginToolResultContext.Result"/>): a hook replaces the result, appends text for the model to it, or
    /// attaches structured details that the call records for the host (timeline cards, event observers) and that the model
    /// never sees. A hook that times out or throws leaves the result as it is. A plugin that does not override this method
    /// is not called.
    /// </remarks>
    /// <param name="context">The tool-result context.</param>
    /// <param name="cancellationToken">Cancelled when the call is cancelled or the hook times out.</param>
    /// <returns><see langword="null"/> or <see cref="PluginToolResult.Unchanged"/> to leave the result; a result made by
    /// <see cref="PluginToolResult.Replace"/>, <see cref="PluginToolResult.Append"/> or <see cref="PluginToolResult.AttachDetails"/> otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask<PluginToolResult?> OnToolResultAsync(
        PluginToolResultContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginToolResult?>((PluginToolResult?)null);
    }

    /// <summary>
    /// A hook called when a run of a session ended normally: the agent answered and calls no more tools. It is not called
    /// for a run that failed or that was stopped.
    /// </summary>
    /// <remarks>
    /// A hook that returns <see cref="PluginAgentRunEndedResult.Continue"/> gives the session a new prompt, recorded as a
    /// prompt of a plugin, which starts the next run: a check that the work is complete, for example. The first plugin
    /// that asks to continue is the one whose prompt is sent. A session continues at most
    /// <see cref="PluginAgentRunEndedContext.MaxContinuations"/> times in a row this way; a prompt from anybody else
    /// starts the count again. A hook that times out (<see cref="HookTimeout"/>) or throws is logged and the session stays
    /// idle. A plugin that does not override this method is not called.
    /// </remarks>
    /// <param name="context">The run-end context.</param>
    /// <param name="cancellationToken">Cancelled when the hook times out or the host closes.</param>
    /// <returns><see langword="null"/> or <see cref="PluginAgentRunEndedResult.Done"/> to leave the session idle.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public virtual ValueTask<PluginAgentRunEndedResult?> OnAgentRunEndedAsync(
        PluginAgentRunEndedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginAgentRunEndedResult?>((PluginAgentRunEndedResult?)null);
    }

    /// <summary>Gets the time CodeAlta waits for a hook of a plugin by default: 10 seconds.</summary>
    public static TimeSpan DefaultHookTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets how long CodeAlta waits for a tool hook (<see cref="OnToolCallAsync"/>, <see cref="OnToolResultAsync"/>) or a
    /// run-end hook (<see cref="OnAgentRunEndedAsync"/>) of the plugin before it goes on without its answer; the hook's
    /// token is cancelled then. <see cref="DefaultHookTimeout"/> by default; a value is kept between 100 milliseconds and
    /// 10 minutes. The host reads it once, when the plugin is activated.
    /// </summary>
    public virtual TimeSpan HookTimeout => DefaultHookTimeout;

    /// <summary>
    /// Gets whether a tool call is blocked when <see cref="OnToolCallAsync"/> of the plugin throws or does not answer in
    /// time, for a check that must not be skipped (a security check): the model reads why. <see langword="false"/> by
    /// default: the failure is logged and the call goes on. The host reads it once, when the plugin is activated.
    /// </summary>
    public virtual bool ToolHooksFailClosed => false;

    /// <summary>
    /// A hook that observes a normalized agent event of any session.
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
    /// Gets whether <see cref="OnAgentEventAsync"/> is given the deltas of streaming content (<see cref="CodeAlta.Agent.AgentContentDeltaEvent"/>):
    /// a session streams one for each fragment of text or of tool output. A plugin that reads only finished content, or that
    /// takes an event as a signal to read the journal (which records no delta), returns <see langword="false"/> and is spared
    /// those calls. The host reads it once, when the plugin is activated.
    /// </summary>
    public virtual bool ObservesContentDeltas => true;

    /// <summary>
    /// Disposes plugin-owned resources.
    /// </summary>
    /// <returns>A task representing asynchronous disposal.</returns>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
