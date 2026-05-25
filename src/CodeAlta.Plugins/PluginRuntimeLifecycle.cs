using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes one active plugin instance.
/// </summary>
public sealed class ActivePluginInstance : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly PluginContributionRegistry _contributionRegistry;
    private readonly PluginRuntimeTaskService _taskService;
    private PluginBase? _instance;
    private PluginAssemblyLoadContext? _loadContext;

    internal ActivePluginInstance(
        PluginBase instance,
        PluginDescriptor descriptor,
        SourcePluginPackage? sourcePackage,
        PluginAssemblyLoadContext? loadContext,
        PluginRuntimeContext runtimeContext,
        IReadOnlyList<PluginContributionRegistration> contributions,
        PluginContributionRegistry contributionRegistry,
        PluginRuntimeTaskService taskService,
        CancellationTokenSource lifetime)
    {
        _instance = instance;
        Descriptor = descriptor;
        SourcePackage = sourcePackage;
        _loadContext = loadContext;
        RuntimeContext = runtimeContext;
        Contributions = contributions;
        _contributionRegistry = contributionRegistry;
        _taskService = taskService;
        _lifetime = lifetime;
        State = PluginRuntimeState.Active;
    }

    /// <summary>Gets the plugin instance while the activation is still holding it.</summary>
    public PluginBase? Instance => _instance;

    /// <summary>Gets the plugin descriptor.</summary>
    public PluginDescriptor Descriptor { get; }

    /// <summary>Gets the source plugin package for dynamic plugins, when available.</summary>
    public SourcePluginPackage? SourcePackage { get; }

    /// <summary>Gets the plugin load context for dynamic plugins while the activation is still holding it.</summary>
    public PluginAssemblyLoadContext? LoadContext => _loadContext;

    /// <summary>Gets the runtime context attached to the plugin.</summary>
    public PluginRuntimeContext RuntimeContext { get; }

    /// <summary>Gets the contribution registrations owned by this activation.</summary>
    public IReadOnlyList<PluginContributionRegistration> Contributions { get; private set; }

    /// <summary>Gets the current runtime state.</summary>
    public PluginRuntimeState State { get; private set; }

    internal CancellationToken LifetimeToken => _lifetime.Token;

    /// <summary>
    /// Deactivates the plugin instance and removes its contributions.
    /// </summary>
    /// <param name="timeout">The bounded deactivation timeout.</param>
    /// <param name="cancellationToken">A token to cancel deactivation.</param>
    /// <returns>Runtime diagnostics raised during deactivation.</returns>
    public async ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> DeactivateAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (State is PluginRuntimeState.Deactivated or PluginRuntimeState.Unloaded)
        {
            return [];
        }

        var diagnostics = await DeactivateManagedAsync(timeout, cancellationToken).ConfigureAwait(false);
        VerifyUnload(diagnostics);
        return diagnostics;
    }

    private async ValueTask<List<PluginRuntimeDiagnostic>> DeactivateManagedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        State = PluginRuntimeState.Deactivating;
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        _contributionRegistry.RemoveByPlugin(Descriptor.RuntimeKey);
        Contributions = [];
        _lifetime.Cancel();
        _taskService.CancelAll();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        try
        {
            await _taskService.WhenIdleAsync(linked.Token).ConfigureAwait(false);
            await DeactivatePluginInstanceAsync(_instance, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            diagnostics.Add(PluginRuntimeDiagnostic.Warning(
                PluginRuntimeDiagnosticSource.Unload,
                "Plugin deactivation timed out.",
                SourcePackage?.PackageId,
                SourcePackage?.PackageDirectory));
        }
        catch (Exception ex)
        {
            diagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Unload,
                $"Plugin deactivation failed: {ex.Message}",
                SourcePackage?.PackageId,
                SourcePackage?.PackageDirectory,
                ex));
        }
        finally
        {
            RuntimeContext.Invalidate();
            State = PluginRuntimeState.Deactivated;
            _instance = null;
        }

        return diagnostics;
    }

    private void VerifyUnload(List<PluginRuntimeDiagnostic> diagnostics)
    {
        var loadContext = _loadContext;
        _loadContext = null;
        if (loadContext is null)
        {
            return;
        }

        var unloadReference = PluginAssemblyLoader.CreateUnloadWeakReference(loadContext);
        loadContext = null;
        var unloaded = PluginAssemblyLoader.VerifyUnload(unloadReference);
        State = unloaded ? PluginRuntimeState.Unloaded : PluginRuntimeState.Failed;
        if (!unloaded)
        {
            diagnostics.Add(PluginRuntimeDiagnostic.Warning(
                PluginRuntimeDiagnosticSource.Unload,
                "Plugin load context did not unload after bounded GC verification. The plugin may still hold references or active tasks.",
                SourcePackage?.PackageId,
                SourcePackage?.PackageDirectory));
        }
    }

    private static async ValueTask DeactivatePluginInstanceAsync(PluginBase? instance, CancellationToken cancellationToken)
    {
        if (instance is null)
        {
            return;
        }

        await instance.OnDeactivatingAsync(cancellationToken).ConfigureAwait(false);
        await instance.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DeactivateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

/// <summary>
/// Describes plugin activation options.
/// </summary>
public sealed record PluginActivationOptions
{
    /// <summary>Gets host information exposed to plugins.</summary>
    public required PluginHostInfo HostInfo { get; init; }

    /// <summary>Gets the host services exposed to plugins.</summary>
    public IPluginServices? Services { get; init; }

    /// <summary>Gets the activation generation.</summary>
    public int ActivationGeneration { get; init; } = 1;
}

/// <summary>
/// Describes the result of plugin activation.
/// </summary>
public sealed record PluginActivationResult
{
    /// <summary>Gets the active plugin instance, when activation succeeded.</summary>
    public ActivePluginInstance? ActivePlugin { get; init; }

    /// <summary>Gets diagnostics raised during activation.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets a value indicating whether activation succeeded.</summary>
    public bool Succeeded => ActivePlugin is not null && Diagnostics.All(static diagnostic => diagnostic.Severity < PluginDiagnosticSeverity.Error);
}

/// <summary>
/// Creates plugin instances, attaches runtime contexts, invokes lifecycle callbacks, and owns contribution cleanup.
/// </summary>
public sealed class PluginRuntimeActivator
{
    private readonly PluginContributionRegistry _contributionRegistry;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRuntimeActivator"/> class.
    /// </summary>
    /// <param name="contributionRegistry">The contribution registry.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="contributionRegistry"/> is <see langword="null"/>.</exception>
    public PluginRuntimeActivator(PluginContributionRegistry contributionRegistry)
    {
        ArgumentNullException.ThrowIfNull(contributionRegistry);
        _contributionRegistry = contributionRegistry;
    }

    /// <summary>
    /// Activates a discovered plugin type.
    /// </summary>
    /// <param name="discoveredType">The discovered plugin type.</param>
    /// <param name="sourcePackage">The source package, when the plugin is dynamic.</param>
    /// <param name="loadContext">The load context, when the plugin is dynamic.</param>
    /// <param name="options">Activation options.</param>
    /// <param name="cancellationToken">A token to cancel activation.</param>
    /// <returns>The activation result.</returns>
    public async ValueTask<PluginActivationResult> ActivateAsync(
        DiscoveredPluginType discoveredType,
        SourcePluginPackage? sourcePackage,
        PluginAssemblyLoadContext? loadContext,
        PluginActivationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discoveredType);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.HostInfo);

        var diagnostics = new List<PluginRuntimeDiagnostic>();
        PluginBase? instance = null;
        try
        {
            instance = (PluginBase?)Activator.CreateInstance(discoveredType.Type);
            if (instance is null)
            {
                throw new InvalidOperationException($"Failed to instantiate plugin type '{discoveredType.Type.FullName}'.");
            }

            var logger = LogManager.GetLogger($"CodeAlta.Plugin.{discoveredType.Descriptor.RuntimeKey}");
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var taskService = new PluginRuntimeTaskService(lifetime.Token);
            var services = new PluginRuntimeServices(
                logger,
                discoveredType.Descriptor.RuntimeKey,
                sourcePackage?.Root.Scope ?? PluginScope.Global,
                sourcePackage?.Root.ProjectId,
                options.Services ?? new NoopPluginServices(logger),
                taskService);
            var context = new PluginRuntimeContext
            {
                Plugin = discoveredType.Descriptor,
                Host = options.HostInfo,
                Logger = logger,
                Services = services,
                PackageDirectory = sourcePackage?.PackageDirectory ?? AppContext.BaseDirectory,
                Scope = sourcePackage?.Root.Scope ?? PluginScope.Global,
                ScopeProjectId = sourcePackage?.Root.ProjectId,
                ScopeProjectPath = sourcePackage?.Root.ProjectPath,
                LifetimeCancellationToken = lifetime.Token,
            };
            instance.AttachRuntimeContext(context);
            await instance.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var contributions = CollectContributions(discoveredType.Descriptor, context, instance, options.ActivationGeneration);
            await instance.OnActivatedAsync(cancellationToken).ConfigureAwait(false);
            var active = new ActivePluginInstance(
                instance,
                discoveredType.Descriptor,
                sourcePackage,
                loadContext,
                context,
                contributions,
                _contributionRegistry,
                taskService,
                lifetime);
            return new PluginActivationResult
            {
                ActivePlugin = active,
                Diagnostics = diagnostics,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (instance is not null)
            {
                try
                {
                    await instance.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _contributionRegistry.RemoveByPlugin(discoveredType.Descriptor.RuntimeKey);
            diagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Activation,
                $"Plugin activation failed: {ex.Message}",
                sourcePackage?.PackageId,
                sourcePackage?.PackageDirectory,
                ex));
            return new PluginActivationResult { Diagnostics = diagnostics };
        }
    }

    private IReadOnlyList<PluginContributionRegistration> CollectContributions(
        PluginDescriptor descriptor,
        PluginRuntimeContext context,
        PluginBase instance,
        int activationGeneration)
    {
        var registrations = new List<PluginContributionRegistration>();
        Add(PluginPoint.Startup, instance.GetStartupContributions());
        Add(PluginPoint.CommandLine, instance.GetCommandLineContributions().Cast<object>());
        Add(PluginPoint.Command, instance.GetCommands());
        Add(PluginPoint.AgentTool, instance.GetAgentTools());
        Add(PluginPoint.AgentBackend, instance.GetAgentBackends());
        Add(PluginPoint.AltaCommand, instance.GetAltaCommands());
        Add(PluginPoint.SystemPrompt, instance.GetSystemPromptContributions());
        Add(PluginPoint.PromptProcessor, instance.GetPromptProcessors());
        Add(PluginPoint.PromptEditor, instance.GetPromptEditorContributions());
        Add(PluginPoint.Compaction, instance.GetCompactionContributions());
        Add(PluginPoint.Ui, instance.GetUiContributions());
        Add(PluginPoint.ThreadEventProjection, instance.GetThreadEventProjections());
        Add(PluginPoint.Resource, instance.GetResources());
        return registrations;

        void Add(PluginPoint point, IEnumerable<object> contributions)
        {
            registrations.AddRange(_contributionRegistry.Register(
                descriptor,
                context.Scope,
                context.ScopeProjectId,
                context.ScopeProjectPath,
                point,
                contributions,
                activationGeneration));
        }
    }
}
