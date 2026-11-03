using System.Net;
using System.Net.Sockets;
using System.Reflection;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Server;
using CodeAlta.Fleet.Update;
using CodeAlta.Hosting;
using CodeAlta.Node.Service;
using XenoAtom.CommandLine;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>The commands of <c>altanode</c>, over the standard streams it is given.</summary>
internal sealed class NodeApplication
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private string[] _args = [];

    /// <summary>Creates the application.</summary>
    /// <param name="output">Standard output: help, status.</param>
    /// <param name="error">Standard error: what went wrong.</param>
    public NodeApplication(TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        _output = output;
        _error = error;
    }

    /// <summary>
    /// Gets or sets whether <c>run</c> sets up the logging of the process (file and console). True for the executable;
    /// tests leave the logging to their assembly.
    /// </summary>
    public bool InitializeLogging { get; init; } = true;

    /// <summary>Gets or sets what changes the options of the host <c>run</c> starts, for tests; null for none.</summary>
    public Func<NodeHostOptions, NodeHostOptions>? ConfigureHost { get; init; }

    /// <summary>Gets or sets what a test is told once <c>run</c> started its node, to stop it; null for nothing.</summary>
    public Action<NodeHost, Action>? Started { get; init; }

    /// <summary>Gets or sets what stops the commands that wait (<c>pair</c>, <c>remote watch</c>, <c>logs --follow</c>), for tests; none by default.</summary>
    public CancellationToken ClientStop { get; init; }

    /// <summary>
    /// Gets or sets what reads the machine <c>install</c>, <c>uninstall</c> and <c>status</c> work on, for tests (temporary
    /// folders, recorded commands); null for this machine.
    /// </summary>
    internal Func<NodeServiceEnvironment?>? ServiceEnvironment { get; init; }

    /// <summary>Gets or sets what finds the executable a service starts, for tests; null for the one of this process.</summary>
    internal Func<NodeExecutable?>? ServiceExecutable { get; init; }

    /// <summary>Gets the HTTP handler of the calls to a CodeAlta Server (<c>join</c>), for tests; null for the usual one.</summary>
    internal HttpMessageHandler? ServerHandler { get; init; }

    /// <summary>Gets what changes the options of the telemetry of <c>run</c>, for tests (their environment, a short interval); null for none.</summary>
    internal Func<NodeTelemetryOptions, NodeTelemetryOptions>? ConfigureTelemetry { get; init; }

    /// <summary>Gets what changes the options of the connectors of the servers of <c>run</c>, for tests; null for none.</summary>
    internal Func<CodeAlta.Fleet.Server.FleetConnectorOptions, CodeAlta.Fleet.Server.FleetConnectorOptions>? ConfigureConnector { get; init; }

    /// <summary>Gets what changes the options of the launcher <c>run</c> hands over to the selected version with, for tests; null for none.</summary>
    internal Func<UpdateLauncherOptions, UpdateLauncherOptions>? ConfigureLauncher { get; init; }

    /// <summary>Gets what changes the options of the self-update of a running node, for tests; null for none.</summary>
    internal Func<NodeUpdatesOptions, NodeUpdatesOptions>? ConfigureUpdates { get; init; }

    /// <summary>Gets the HTTP handler of the feeds of <c>update</c>, for tests; null for the usual one.</summary>
    internal HttpMessageHandler? UpdateHandler { get; init; }

    /// <summary>Gets the folder of the application of this process, for tests; null for <see cref="AppContext.BaseDirectory"/>.</summary>
    internal string? ApplicationDirectory { get; init; }

    /// <summary>Runs a command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code (<see cref="NodeExitCodes"/>).</returns>
    public async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        _args = args;
        if (args.Length == 0)
        {
            await Create(NodeCommandHandlers.Placeholders).RunAsync(["--help"], new CommandRunConfig { Out = _output, Error = _error }).ConfigureAwait(false);
            return NodeExitCodes.InvalidUsage;
        }

        // Parsed first by a tree of its own (its callbacks fill options that are thrown away): an invalid command line has
        // its exit code, 2.
        var parse = Create(NodeCommandHandlers.Placeholders).Parse(args, new CommandRunConfig { Out = TextWriter.Null, Error = TextWriter.Null });
        if (parse.HasErrors)
        {
            foreach (var error in parse.Errors) await _error.WriteLineAsync("altanode: " + error.Message).ConfigureAwait(false);
            await _error.WriteLineAsync("Run altanode --help for the commands, or altanode <command> --help for their options.").ConfigureAwait(false);
            return NodeExitCodes.InvalidUsage;
        }

        try
        {
            var client = new NodeClientCommands(_output, _error) { Stop = ClientStop };
            var service = new NodeServiceCommands(_output, _error, ServiceEnvironment ?? NodeServiceEnvironment.Current, ServiceExecutable ?? LocateExecutable)
            {
                Stop = ClientStop,
                InstalledVersion = InstalledVersion(),
            };
            var devices = new NodeDeviceCommands(_output, _error) { Stop = ClientStop };
            var logs = new NodeLogCommand(_output, _error) { Stop = ClientStop };
            var projects = new NodeProjectCommands(_output, _error, CloneRootOf) { Stop = ClientStop };
            var servers = new NodeServerCommands(_output, _error) { Stop = ClientStop, Handler = ServerHandler };
            var update = new NodeUpdateCommands(_output, _error, Version, InstalledVersion()) { Stop = ClientStop, Handler = UpdateHandler };
            return await Create(new NodeCommandHandlers(RunNodeAsync, service.StatusAsync, NotImplementedAsync, client.PairAsync, client.MachinesAsync,
                    client.ConnectAsync, client.RemoteAsync, service.InstallAsync, service.UninstallAsync, logs.RunAsync, devices.DevicesAsync,
                    devices.RevokeAsync, devices.PermissionsAsync, client.MovesAsync, projects.RunAsync, servers.JoinAsync, update.UpdateAsync))
                .RunAsync(args, new CommandRunConfig { Out = _output, Error = _error }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _error.WriteLineAsync("altanode: " + NodeClientCommands.Describe(exception)).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    private static CommandApp Create(NodeCommandHandlers handlers) => NodeCommandLine.Create(handlers);

    // The clone root a node of the profile runs with: the one of its options, as run would start it.
    private string? CloneRootOf(CodeAltaInstanceProfile profile)
    {
        var options = new NodeHostOptions { Profile = profile };
        return (ConfigureHost?.Invoke(options) ?? options).CloneRoot;
    }

    private static NodeExecutable? LocateExecutable()
        => NodeExecutableLocator.Locate(Environment.ProcessPath, Path.Combine(AppContext.BaseDirectory, "altanode.dll"), static path => File.Exists(path) || Directory.Exists(path));

    /// <summary>The version of CodeAlta this executable is.</summary>
    internal static string Version
        => typeof(NodeApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    // Where run listens: loopback by default; nowhere with --no-listen, which takes no other listening option.
    internal static NodeListenOptions? ResolveListen(NodeRunOptions options, out string? error)
    {
        error = null;
        if (options.NoListen)
        {
            if (options.Port is not null || options.Bind is not null || options.ListenAll)
            {
                error = "--no-listen disables listening: --port, --bind and --listen-all cannot go with it.";
                return null;
            }

            return new NodeListenOptions { Enabled = false };
        }

        if (options.Bind is not null && options.ListenAll)
        {
            error = "--bind and --listen-all cannot go together.";
            return null;
        }

        var listen = new NodeListenOptions();
        if (options.Port is { } port)
        {
            if (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number > 65535)
            {
                error = $"--port takes 0 to 65535: '{port}'.";
                return null;
            }

            listen = listen with { Port = number };
        }

        if (options.Bind is { } bind)
        {
            if (!IPAddress.TryParse(bind, out var address))
            {
                error = $"--bind takes an IP address of this machine: '{bind}'.";
                return null;
            }

            listen = listen with { Address = address };
        }
        else if (options.ListenAll)
        {
            listen = listen with { Address = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any };
        }

        return listen;
    }

    private async ValueTask<int> NotImplementedAsync(string command)
    {
        await _error.WriteLineAsync($"altanode {command}: not implemented yet.").ConfigureAwait(false);
        return NodeExitCodes.NotImplemented;
    }

    private async ValueTask<int> UsageAsync(string message)
    {
        await _error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }

    private async ValueTask<int> RunNodeAsync(NodeRunOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (ResolveListen(options, out var listenError) is not { } listen) return await UsageAsync(listenError!).ConfigureAwait(false);
        var name = options.Name ?? Environment.MachineName;
        try
        {
            name = TrustedDeviceStore.NormalizeName(name);
        }
        catch (ArgumentException exception)
        {
            return await UsageAsync("--name: " + exception.Message).ConfigureAwait(false);
        }

        // The installed entry point (the tool, what the service starts) hands over to the version altanode update
        // selected, and passes its exit code through; a switch that waited for the next start is taken first. It holds the
        // lease of the launcher from here to its end, so that a second run never selects, starts or rolls back a version
        // while this one does: it is refused at once, even in the moment the version this run started has not taken the
        // profile yet. Not while another instance holds the profile: this run is refused below as before, and the version
        // on trial of the running node is neither started twice nor blamed for that refusal.
        var store = NodeUpdatePaths.Store(profile);
        UpdateLauncherLease? lease = null;
        if (!UpdateLauncher.IsSupervised && !store.Contains(ApplicationDirectory ?? AppContext.BaseDirectory))
        {
            try
            {
                lease = store.TryAcquireLauncherLease();
            }
            catch (UpdateException exception)
            {
                await _error.WriteLineAsync($"altanode: {exception.Message}").ConfigureAwait(false);
                return NodeExitCodes.Failure;
            }

            if (lease is null)
            {
                await _error.WriteLineAsync($"altanode: another altanode run holds the profile {profile.StateRoot} (it runs the node, or starts it).").ConfigureAwait(false);
                return NodeExitCodes.ProfileInUse;
            }
        }

        using var heldLease = lease;
        if (lease is not null && !NodeProfileStatus.Read(profile).IsHeld)
        {
            try
            {
                store.TakePending(UpdateProduct.Node);
            }
            catch (UpdateException exception)
            {
                await _error.WriteLineAsync($"altanode: the versions of {store.Root} could not be read: {exception.Message}").ConfigureAwait(false);
            }

            if (store.Read() is { } selected && (selected.Current is not null || selected.RollingBack is not null)) return await RunLauncherAsync(store, lease).ConfigureAwait(false);
        }

        var (exitCode, restart) = await RunInProcessAsync(options, profile, listen, name, store).ConfigureAwait(false);
        if (!restart) return exitCode;
        // A launcher waits for this process and starts the newly selected version; without one, this process becomes it.
        if (UpdateLauncher.IsSupervised) return UpdateLauncher.RestartExitCode;
        return await RunLauncherAsync(store, lease).ConfigureAwait(false);
    }

    // The launcher hop: the selected version runs as a child process of this one, which ends with its exit code.
    private async Task<int> RunLauncherAsync(UpdateVersionStore store, UpdateLauncherLease? lease)
    {
        var options = new UpdateLauncherOptions
        {
            Store = store, Product = UpdateProduct.Node, Arguments = _args, Installed = InstalledCommand(), InstalledVersion = InstalledVersion(),
            Lease = lease, HeldExitCode = NodeExitCodes.ProfileInUse,
            Log = message => _error.WriteLine("altanode: " + message),
        };
        if (ConfigureLauncher is not null) options = ConfigureLauncher(options);
        return await new UpdateLauncher(options).RunAsync().ConfigureAwait(false);
    }

    // How this process was started, to start the installed entry point again when the pointer returns to it.
    private UpdateCommand InstalledCommand()
    {
        var path = Environment.ProcessPath ?? "altanode";
        return string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new UpdateCommand(path, [Path.Combine(ApplicationDirectory ?? AppContext.BaseDirectory, UpdateProduct.Node.AssemblyFileName)])
            : new UpdateCommand(path, []);
    }

    // The version of the installed entry point: this one, unless this process runs a version of a folder of versions.
    private string? InstalledVersion()
    {
        if (UpdateLauncher.SupervisorInstalledVersion is { } passed) return passed;
        var directory = Path.TrimEndingDirectorySeparator(ApplicationDirectory ?? AppContext.BaseDirectory);
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "versions", StringComparison.OrdinalIgnoreCase) ? null : Version;
    }

    // Whether the node is healthy, for a version on trial: it listens when it should, and its connections to the
    // servers it joined (and that approved it) are up.
    private static bool IsHealthy(NodeFleet fleet, NodeListenOptions listen, NodeServers servers)
        => (!listen.Enabled || fleet.EndPoint is not null)
            && servers.Connections.Connections.Where(static connection => connection.Link.State == FleetServerLinkStates.Approved)
                .All(static connection => connection.Connector?.State == FleetConnectorState.Online);

    private async Task<(int ExitCode, bool Restart)> RunInProcessAsync(NodeRunOptions options, CodeAltaInstanceProfile profile, NodeListenOptions listen, string name,
        UpdateVersionStore store)
    {
        var ownsLogging = false;
        NodeTelemetry? telemetry = null;
        NodeHost? running = null;
        NodeFleet? listening = null;
        NodeServers? linked = null;
        try
        {
            var hostOptions = new NodeHostOptions
            {
                Profile = profile, Offline = options.Offline, StartPlugins = !options.NoPlugins,
                // The log and the telemetry of the state root start once its lock is held: a refused start leaves them to its instance.
                Admitted = () =>
                {
                    telemetry = StartTelemetry(profile, () => running, () => listening, () => linked);
                    ownsLogging = InitializeLogging && NodeLogging.Initialize(profile.StateRoot, console: true, telemetry.LogWriter);
                },
            };
            if (ConfigureHost is not null) hostOptions = ConfigureHost(hostOptions);
            using var stopRequested = new CancellationTokenSource();
            using var hurry = new CancellationTokenSource();
            NodeHost node;
            using (var startSignals = new NodeSignals(stopRequested.Cancel, stopRequested.Cancel, static () => Environment.Exit(NodeExitCodes.Failure)))
            {
                try
                {
                    node = await NodeHost.StartAsync(hostOptions, stopRequested.Token).ConfigureAwait(false);
                }
                catch (CodeAltaAlreadyRunningException)
                {
                    var status = NodeProfileStatus.Read(profile);
                    await _error.WriteLineAsync($"altanode: the profile {profile.StateRoot} is in use. {status.Describe()}").ConfigureAwait(false);
                    await _error.WriteLineAsync("One CodeAlta instance runs per profile: stop it first, or run the developer instance with --dev.").ConfigureAwait(false);
                    return (NodeExitCodes.ProfileInUse, false);
                }
                catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
                {
                    await _error.WriteLineAsync("altanode: stopped while starting.").ConfigureAwait(false);
                    return (NodeExitCodes.Success, false);
                }
            }

            running = node;
            await using (node.ConfigureAwait(false))
            {
                NodeFleet fleet;
                NodeUpdates? updates = null;
                try
                {
                    // The node API runs with or without a listener (--no-listen): the servers the node joined relay channels to it.
                    fleet = NodeFleet.Start(node, listen, name, Version, stopRequested.Cancel, () => updates?.Status(), telemetry?.Metrics);
                    listening = fleet;
                }
                catch (SocketException exception)
                {
                    await _error.WriteLineAsync($"altanode: cannot listen on {FleetDirectConnector.Format(listen.Address, listen.Port)}: {exception.Message}").ConfigureAwait(false);
                    await node.StopAsync(hurry.Token).ConfigureAwait(false);
                    return (NodeExitCodes.Failure, false);
                }

                using var fleetLifetime = new FleetLifetime(fleet);
                // The servers the node joined (altanode join): one control connection each, beside the listener or without it.
                await using var servers = NodeServers.Start(node, fleet.Service, name, Version, ConfigureConnector, telemetry);
                linked = servers;
                // The self-update: a version on trial confirms itself once healthy; a switch altanode update asks for
                // stops the node when it is idle, so that its launcher starts the new version.
                if (UpdateVersion.TryParse(Version, out var runningVersion))
                {
                    var updateOptions = new NodeUpdatesOptions
                    {
                        Store = store, Running = runningVersion, Installed = InstalledVersion(), SessionsAtWork = node.Host.RuntimeService.CountSessionsAtWork,
                        PauseAdmission = node.PauseAdmission, Healthy = () => IsHealthy(fleet, listen, servers), RequestRestart = stopRequested.Cancel,
                        PolicyPath = NodeUpdatePaths.PolicyPath(profile), Log = static message => NodeLog(message),
                    };
                    updates = new NodeUpdates(ConfigureUpdates?.Invoke(updateOptions) ?? updateOptions);
                    updates.Start();
                }

                using var signals = new NodeSignals(stopRequested.Cancel, hurry.Cancel, static () => Environment.Exit(NodeExitCodes.Failure));
                Started?.Invoke(node, stopRequested.Cancel);
                await _output.WriteLineAsync($"CodeAlta Node runs on {profile.StateRoot}. Ctrl+C stops it.").ConfigureAwait(false);
                await _output.WriteLineAsync(fleet.EndPoint is not { } endPoint
                    ? "It listens for no device (--no-listen); the servers it joined relay their channels."
                    : $"{name} ({fleet.DeviceId}) listens for paired devices on {endPoint}; altanode pair makes a pairing link.").ConfigureAwait(false);
                try
                {
                    await Task.Delay(Timeout.Infinite, stopRequested.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                var restart = updates?.RestartRequested == true;
                await _output.WriteLineAsync(restart ? "Stopping CodeAlta Node to start its new version..." : "Stopping CodeAlta Node...").ConfigureAwait(false);
                if (updates is not null) await updates.DisposeAsync().ConfigureAwait(false);
                // The channels close first: no remote call reaches a runtime that stops.
                await fleetLifetime.StopAsync().ConfigureAwait(false);
                await servers.DisposeAsync().ConfigureAwait(false);
                await node.StopAsync(hurry.Token).ConfigureAwait(false);
                return (NodeExitCodes.Success, restart);
            }
        }
        finally
        {
            // The last spans, metrics and records go before the log closes.
            telemetry?.Dispose();
            if (ownsLogging) NodeLogging.Shutdown();
        }
    }

    private static void NodeLog(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Node").Info(message);
    }

    // The telemetry of the node: its own file, its variables and the settings its servers pushed, with the gauges of what it runs.
    private NodeTelemetry StartTelemetry(CodeAltaInstanceProfile profile, Func<NodeHost?> node, Func<NodeFleet?> fleet, Func<NodeServers?> servers)
    {
        var options = new NodeTelemetryOptions
        {
            FleetRoot = new CodeAlta.Catalog.CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot }.FleetRoot,
            Version = Version,
            SessionsWorking = () => node()?.Host.RuntimeService.CountSessionsAtWork() ?? 0,
            ConnectedDevices = () => fleet()?.Service.ConnectionCount ?? 0,
            ServersOnline = () => servers()?.Connections.Connections.Count(static connection => connection.Connector?.State == CodeAlta.Fleet.Server.FleetConnectorState.Online) ?? 0,
        };
        return new NodeTelemetry(ConfigureTelemetry?.Invoke(options) ?? options);
    }

    // Stops the fleet part of the node before the node, and on any way out of run.
    private sealed class FleetLifetime(NodeFleet? fleet) : IDisposable
    {
        private NodeFleet? _fleet = fleet;

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _fleet, null) is { } running) await running.DisposeAsync().ConfigureAwait(false);
        }

        public void Dispose() => StopAsync().GetAwaiter().GetResult();
    }
}
