using System.Diagnostics;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>
/// CodeAlta Node: a <see cref="CodeAltaHost"/> without a window that owns its profile. It takes the lock of the state
/// root (<c>alta.lock</c>) as CodeAlta Desktop and TUI do, so it is the one instance of its profile, and composes the
/// host with the configured providers, the plugins made for hosts without a user interface, the agent tools of CodeAlta
/// CLI (the <c>alta</c> tool and Code Mode), and the sessions other processes write followed.
/// </summary>
/// <remarks>
/// Permission requests of its sessions are never granted by the node itself: they wait in the runtime's permission
/// service (<see cref="Orchestration.Runtime.SessionRuntimeService.Permissions"/>) for a person to answer them, and
/// are refused where nobody can be asked. Stopped by <see cref="StopAsync"/>: it accepts no more work, gives running
/// turns a bound to finish, then disposes the host and releases the lock.
/// </remarks>
public sealed class NodeHost : IAsyncDisposable
{
    private readonly NodeHostOptions _options;
    private readonly CodeAltaSingleInstanceGuard _guard;
    private readonly ModelsDevCatalogService _modelsDev;
    private readonly LoopbackHarnessToolEndpoints _toolEndpoints;
    private readonly HeadlessAltaTools _alta;
    private readonly Logger _logger;
    private readonly object _gate = new();
    private Task? _stop;
    private int _acceptsWork = 1;
    private int _admissionPaused;

    private NodeHost(NodeHostOptions options, CodeAltaSingleInstanceGuard guard, CodeAltaHost host, HeadlessAltaTools alta,
        ModelsDevCatalogService modelsDev, LoopbackHarnessToolEndpoints toolEndpoints, Logger logger)
    {
        _options = options;
        _guard = guard;
        Host = host;
        _alta = alta;
        _modelsDev = modelsDev;
        _toolEndpoints = toolEndpoints;
        _logger = logger;
    }

    /// <summary>Gets the runtime of the node.</summary>
    public CodeAltaHost Host { get; }

    /// <summary>
    /// Gets the dispatcher of the <c>alta</c> commands, the one of the <c>alta</c> tool of the sessions of the node
    /// (<see cref="AltaCommandRegistryOptions.Node"/>).
    /// </summary>
    public AltaCommandDispatcher Alta => _alta.Dispatcher;

    /// <summary>Gets the profile the node owns.</summary>
    public CodeAltaInstanceProfile Profile => _options.Profile;

    /// <summary>Gets the options the node started with.</summary>
    public NodeHostOptions Options => _options;

    /// <summary>Gets the lock the node holds while it runs.</summary>
    public string LockFilePath => _guard.LockFilePath;

    /// <summary>
    /// Gets whether the node accepts new work. False from the start of <see cref="StopAsync"/>, and while admission is
    /// paused (<see cref="PauseAdmission"/>): what admits work from outside (the node API, automations) checks it.
    /// </summary>
    public bool AcceptsWork => Volatile.Read(ref _acceptsWork) == 1 && Volatile.Read(ref _admissionPaused) == 0;

    /// <summary>Gets whether admission is paused while the node drains its sessions to switch to a new version.</summary>
    public bool AdmissionPaused => Volatile.Read(ref _admissionPaused) == 1 && Volatile.Read(ref _acceptsWork) == 1;

    /// <summary>
    /// Pauses or resumes the admission of new work from outside, while the node drains its sessions to switch to a new
    /// version (<c>altanode update</c>): the node API refuses new work with <c>node.updating</c>, which callers retry.
    /// </summary>
    /// <param name="paused">Whether new work is refused.</param>
    public void PauseAdmission(bool paused) => Volatile.Write(ref _admissionPaused, paused ? 1 : 0);

    /// <summary>Takes the lock of the profile and starts the host.</summary>
    /// <param name="options">The profile and the options of the node.</param>
    /// <param name="cancellationToken">Cancels the start; what was acquired is released.</param>
    /// <returns>The running node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="CodeAltaAlreadyRunningException">Another instance holds the profile.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="Exception">The host could not be created; the lock and what was acquired are released first.</exception>
    public static async Task<NodeHost> StartAsync(NodeHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var profile = options.Profile;
        Directory.CreateDirectory(profile.GlobalRoot);
        // One instance per profile: the lock is taken before anything of the profile is read or written.
        var guard = CodeAltaSingleInstanceGuard.Acquire(profile.LockFilePath);
        LoopbackHarnessToolEndpoints? toolEndpoints = null;
        ModelsDevCatalogService? modelsDev = null;
        CodeAltaHost? host = null;
        try
        {
            options.Admitted?.Invoke();
            var logger = LogManager.GetLogger("CodeAlta.Node");
            toolEndpoints = new LoopbackHarnessToolEndpoints(LogManager.GetLogger("CodeAlta.Node.ToolEndpoints"));
            modelsDev = new ModelsDevCatalogService(new ModelsDevCatalogServiceOptions
            {
                CacheFilePath = Path.Combine(profile.GlobalRoot, "cache", "model-catalog", "models_dev_db.json"),
            });
            if (!options.Offline && options.ConfigureModelProviders is null) modelsDev.StartBackgroundRefresh();
            var pluginProviderTypes = new PluginModelProviderTypes(toolEndpoints) { ModelCatalog = modelsDev };
            host = await CodeAltaHost.CreateAsync(CreateHostOptions(options, guard, modelsDev, pluginProviderTypes), cancellationToken).ConfigureAwait(false);
            var alta = HeadlessAltaTools.Attach(host, CreateAltaOptions(profile));
            logger.Info($"CodeAlta Node started on {profile.StateRoot} (PID {Environment.ProcessId}).");
            return new NodeHost(options, guard, host, alta, modelsDev, toolEndpoints, logger);
        }
        catch
        {
            try
            {
                if (host is not null) await host.DisposeAsync().ConfigureAwait(false);
                if (toolEndpoints is not null) await toolEndpoints.DisposeAsync().ConfigureAwait(false);
                if (modelsDev is not null) await modelsDev.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                guard.Dispose();
            }

            throw;
        }
    }

    /// <summary>The composition of the host of a node, as CodeAlta TUI composes its own, without a window.</summary>
    internal static CodeAltaHostOptions CreateHostOptions(NodeHostOptions options, CodeAltaSingleInstanceGuard? guard,
        ModelsDevCatalogService modelsDev, PluginModelProviderTypes pluginProviderTypes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modelsDev);
        ArgumentNullException.ThrowIfNull(pluginProviderTypes);
        var profile = options.Profile;
        var catalog = new CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot };
        return new CodeAltaHostOptions
        {
            GlobalRoot = profile.GlobalRoot,
            StateRoot = profile.StateRoot,
            // The node owns its state root as CodeAlta TUI does: the scratchpad left by its last run is cleared.
            StartupScratchpadCleanup = new ScratchpadStartupCleanup(profile, guard),
            CurrentProjectPath = options.CurrentProjectPath ?? Environment.CurrentDirectory,
            DiscoveryScope = options.DiscoveryScope,
            BuiltInSkillRoot = options.BuiltInSkillRoot,
            IsHeadless = true,
            HasInteractiveUi = false,
            OwnsLogging = false,
            // Permissions: a node is NOT a headless run of altacli. FullTrust, which grants every request, is never used:
            // the sessions of a node act for people who are elsewhere, and only they may allow what an agent asks.
            // - every owned send reviews its requests: they stay pending in the runtime's permission service
            //   (RuntimeService.Permissions), where the node API answers them for a person (a later part of the fleet);
            // - nothing is approved automatically, whatever the permission mode of a session (no session modes);
            // - a request outside a send, which nobody can be asked about, is denied.
            FullTrust = false,
            AutoApproveOwnedPermissions = false,
            ReviewOwnedCommandPermissions = true,
            SessionPermissionModes = false,
            // A session works under the Git, GitHub and CLI identity of its space, as in CodeAlta Desktop.
            AppliesSpaceIdentities = true,
            // The sessions it runs are leased, so that the command line does not run them meanwhile; the sessions the
            // command line writes are listed and followed.
            SessionLeaseOwner = SessionLeaseOwner.NodeKind,
            FollowExternalSessions = true,
            // The plugins made for hosts without a user interface: the built-in ones below, and source plugins that
            // declare the command-line application.
            StartPlugins = options.StartPlugins,
            PluginFrontend = PluginFrontends.Cli,
            PluginBuiltIns = NodeBuiltInPlugins.All,
            RawArguments = [],
            ConfigureModelProviders = options.ConfigureModelProviders ?? (registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                registry, new CodeAltaConfigStore(catalog), profile.GlobalRoot, modelsDev, pluginProviderTypes)),
            ConfigurePluginModelProviders = options.ConfigureModelProviders is not null ? null : (registry, runtime) =>
            {
                pluginProviderTypes.Attach(runtime.GetModelProviderTypes);
                pluginProviderTypes.RegisterOrReplace(registry, new CodeAltaConfigStore(catalog), profile.GlobalRoot, includeDisabled: false);
            },
        };
    }

    /// <summary>
    /// The agent tools of the sessions of a node, as CodeAlta CLI gives them, for a host that outlives their turns: the
    /// <c>alta</c> commands of <see cref="AltaCommandRegistryOptions.Node"/> (reminders and automations, no <c>ask</c> nor
    /// <c>review</c> until they reach people), and Code Mode as the configuration sets it, whose scripts keep their values
    /// and their source in the profile the node owns. Nobody answers the questions of a script.
    /// </summary>
    internal static HeadlessAltaToolsOptions CreateAltaOptions(CodeAltaInstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new HeadlessAltaToolsOptions
        {
            Registry = AltaCommandRegistryOptions.Node,
            CodeModeScratchpadRoot = Path.Combine(profile.StateRoot, "scratchpad"),
            KeepsScriptValues = true,
            ReadsSessionSpaces = true,
            OwnsReminders = true,
        };
    }

    /// <summary>
    /// Stops the node: it accepts no more work, gives the sessions at work <see cref="NodeHostOptions.DrainLimit"/> to
    /// finish, disposes the host (which stops what still runs; an interrupted session is resumable), and releases the
    /// lock. Repeated calls share the first stop.
    /// </summary>
    /// <param name="drainCancellation">Ends the wait for running turns early (a second Ctrl+C): the host is disposed at once.</param>
    /// <returns>A task that completes once the lock is released.</returns>
    public Task StopAsync(CancellationToken drainCancellation)
    {
        lock (_gate)
        {
            return _stop ??= StopCoreAsync(drainCancellation);
        }
    }

    /// <summary>Stops the node as <see cref="StopAsync"/> does.</summary>
    /// <returns>A task that completes once the lock is released.</returns>
    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task StopCoreAsync(CancellationToken drainCancellation)
    {
        Volatile.Write(ref _acceptsWork, 0);
        try
        {
            // No reminder fires once the node stops: it starts no more work by itself.
            try
            {
                await _alta.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _logger.Error(failure, "The reminders failed while they stopped.");
            }

            await DrainAsync(drainCancellation).ConfigureAwait(false);
            try
            {
                await Host.DisposeAsync().AsTask().WaitAsync(_options.ShutdownLimit).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warn($"The host did not stop within {(int)_options.ShutdownLimit.TotalSeconds} s: the process ends what is left.");
            }
            catch (Exception failure)
            {
                _logger.Error(failure, "The host failed while it stopped.");
            }

            await _toolEndpoints.DisposeAsync().ConfigureAwait(false);
            await _modelsDev.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _guard.Dispose();
            _logger.Info("CodeAlta Node stopped.");
        }
    }

    // Waits until no session is at work (a run in flight, a background job, a script), within the drain limit.
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var runtime = Host.RuntimeService;
        var atWork = runtime.CountSessionsAtWork();
        if (atWork == 0) return;
        _logger.Info($"Stopping: waiting up to {(int)_options.DrainLimit.TotalSeconds} s for {atWork} session(s) at work.");
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (atWork > 0 && elapsed.Elapsed < _options.DrainLimit)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                atWork = runtime.CountSessionsAtWork();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if (atWork > 0) _logger.Warn($"Stopping {atWork} session(s) still at work; they can be resumed.");
    }
}
