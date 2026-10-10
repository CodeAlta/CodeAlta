using System.Runtime.CompilerServices;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes one active plugin instance.
/// </summary>
public sealed partial class ActivePluginInstance : IAsyncDisposable
{
    private readonly PluginActivationLifetime _lifetime;
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
        PluginActivationLifetime lifetime)
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
    /// <param name="timeout">Bounds only this caller's wait, never the retained original or dependency lifetime.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    /// <returns>Runtime diagnostics raised during deactivation.</returns>
    /// <exception cref="InvalidOperationException">The caller would join its own outstanding event attempt.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is invalid.</exception>
    /// <exception cref="OperationCanceledException">The caller's wait was cancelled.</exception>
    /// <exception cref="Exception">The retained prerequisite or deactivation failed; dependencies are retained.</exception>
    public ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> DeactivateAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => WaitDeactivationAsync(timeout, cancellationToken);

    private void VerifyUnload(List<PluginRuntimeDiagnostic> diagnostics)
    {
        var unloadReference = DetachLoadContext();
        if (unloadReference is null)
        {
            return;
        }

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

    // Keep the last strong local out of the stack frame that forces GC. Assigning null in that
    // frame is not sufficient: the JIT may keep the local live through unload verification.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? DetachLoadContext()
    {
        var loadContext = _loadContext;
        _loadContext = null;
        return loadContext is null ? null : PluginAssemblyLoader.CreateUnloadWeakReference(loadContext);
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
    public ValueTask DisposeAsync() => new(DeactivateOriginalAsync());
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

    /// <summary>Gets the application database the plugins get their tables from, or <see langword="null"/> for a host without one.</summary>
    public IApplicationDatabase? ApplicationDatabase { get; init; }

    /// <summary>Gets the explicitly composed built-in factory; source plugins retain type-based activation.</summary>
    internal Func<PluginBase>? BuiltInFactory { get; init; }
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
            instance = CreateInstance(options.BuiltInFactory, () => (PluginBase?)Activator.CreateInstance(discoveredType.Type));
            if (instance is null)
            {
                throw new InvalidOperationException($"Failed to instantiate plugin type '{discoveredType.Type.FullName}'.");
            }

            if (instance.GetType() != discoveredType.Type)
            {
                throw new InvalidOperationException($"Plugin factory returned type '{instance.GetType().FullName}' instead of '{discoveredType.Type.FullName}'.");
            }

            var logger = LogManager.GetLogger($"CodeAlta.Plugin.{discoveredType.Descriptor.RuntimeKey}");
            var lifetime = new PluginActivationLifetime(cancellationToken);
            var taskService = new PluginRuntimeTaskService(lifetime.Token);
            var hostServices = options.Services ?? new NoopPluginServices(logger);
            // A host that keeps no plugin data of its own gives each plugin a folder in the CodeAlta home.
            var state = hostServices.State is NoopPluginStateStore && !string.IsNullOrWhiteSpace(options.HostInfo.UserDataDirectory)
                ? new PluginFileStateStore(Path.Combine(options.HostInfo.UserDataDirectory, "plugin-data"), discoveredType.Descriptor.RuntimeKey, sourcePackage?.Root.ProjectPath, hostServices)
                : null;
            // The tables of this plugin: its key decides the prefix, so it finds them again after a restart.
            IPluginDatabase? database = options.ApplicationDatabase is { } applicationDatabase
                ? new PluginDatabase(applicationDatabase, discoveredType.Descriptor.RuntimeKey, lifetime.Token)
                : null;
            var services = new PluginRuntimeServices(
                logger,
                discoveredType.Descriptor.RuntimeKey,
                sourcePackage?.Root.Scope ?? PluginScope.Global,
                sourcePackage?.Root.ProjectId,
                hostServices,
                taskService,
                state,
                database);
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

    // No retry or reflection fallback after a supplied factory returns null or throws (including cancellation).
    internal static PluginBase? CreateInstance(Func<PluginBase>? builtInFactory, Func<PluginBase?> createFromType)
        => builtInFactory is not null ? builtInFactory() : createFromType();

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
        Add(PluginPoint.AltaCommand, instance.GetAltaCommands());
        Add(PluginPoint.SystemPrompt, instance.GetSystemPromptContributions());
        Add(PluginPoint.PromptProcessor, instance.GetPromptProcessors());
        Add(PluginPoint.InstructionProcessor, instance.GetInstructionProcessors());
        Add(PluginPoint.PromptEditor, instance.GetPromptEditorContributions());
        Add(PluginPoint.PromptPicker, instance.GetPromptPickers());
        Add(PluginPoint.Compaction, instance.GetCompactionContributions());
        Add(PluginPoint.Ui, instance.GetUiContributions());
        Add(PluginPoint.SessionEventProjection, instance.GetSessionEventProjections());
        Add(PluginPoint.Resource, instance.GetResources());
        Add(PluginPoint.Canvas, instance.GetCanvases());
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
