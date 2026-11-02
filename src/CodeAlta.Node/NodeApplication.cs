using System.Net;
using System.Net.Sockets;
using System.Reflection;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Hosting;
using XenoAtom.CommandLine;

namespace CodeAlta.Node;

/// <summary>The commands of <c>altanode</c>, over the standard streams it is given.</summary>
internal sealed class NodeApplication
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;

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

    /// <summary>Gets or sets what stops the commands that wait (<c>pair</c>, <c>remote watch</c>), for tests; none by default.</summary>
    public CancellationToken ClientStop { get; init; }

    /// <summary>Runs a command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code (<see cref="NodeExitCodes"/>).</returns>
    public async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            await Create(Placeholders()).RunAsync(["--help"], new CommandRunConfig { Out = _output, Error = _error }).ConfigureAwait(false);
            return NodeExitCodes.InvalidUsage;
        }

        // Parsed first by a tree of its own (its callbacks fill options that are thrown away): an invalid command line has
        // its exit code, 2.
        var parse = Create(Placeholders()).Parse(args, new CommandRunConfig { Out = TextWriter.Null, Error = TextWriter.Null });
        if (parse.HasErrors)
        {
            foreach (var error in parse.Errors) await _error.WriteLineAsync("altanode: " + error.Message).ConfigureAwait(false);
            await _error.WriteLineAsync("Run altanode --help for the commands, or altanode <command> --help for their options.").ConfigureAwait(false);
            return NodeExitCodes.InvalidUsage;
        }

        try
        {
            var client = new NodeClientCommands(_output, _error) { Stop = ClientStop };
            return await Create(new NodeCommandHandlers(RunNodeAsync, StatusAsync, NotImplementedAsync, client.PairAsync, client.MachinesAsync,
                    client.ConnectAsync, client.RemoteAsync))
                .RunAsync(args, new CommandRunConfig { Out = _output, Error = _error }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _error.WriteLineAsync("altanode: " + NodeClientCommands.Describe(exception)).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    private static CommandApp Create(NodeCommandHandlers handlers) => NodeCommandLine.Create(handlers);

    private static NodeCommandHandlers Placeholders()
        => new(static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0),
            static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0));

    /// <summary>The version of CodeAlta this executable is.</summary>
    internal static string Version
        => typeof(NodeApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    // Where run listens: loopback by default; nowhere with --no-listen, which takes no other listening option.
    private static NodeListenOptions? ResolveListen(NodeRunOptions options, out string? error)
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

    private async ValueTask<int> StatusAsync(NodeProfileOptions options)
    {
        if (options.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var status = NodeProfileStatus.Read(profile);
        await _output.WriteLineAsync($"Profile: {profile.StateRoot}").ConfigureAwait(false);
        await _output.WriteLineAsync(status.Describe()).ConfigureAwait(false);
        return status.IsHeld ? NodeExitCodes.Success : NodeExitCodes.Failure;
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

        var ownsLogging = false;
        try
        {
            var hostOptions = new NodeHostOptions
            {
                Profile = profile, Offline = options.Offline, StartPlugins = !options.NoPlugins,
                // The log of the state root is opened once its lock is held: a refused start leaves it to its instance.
                Admitted = () => ownsLogging = InitializeLogging && NodeLogging.Initialize(profile.StateRoot, console: true),
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
                    return NodeExitCodes.ProfileInUse;
                }
                catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
                {
                    await _error.WriteLineAsync("altanode: stopped while starting.").ConfigureAwait(false);
                    return NodeExitCodes.Success;
                }
            }

            await using (node.ConfigureAwait(false))
            {
                NodeFleet? fleet = null;
                if (listen.Enabled)
                {
                    try
                    {
                        fleet = NodeFleet.Start(node, listen, name, Version);
                    }
                    catch (SocketException exception)
                    {
                        await _error.WriteLineAsync($"altanode: cannot listen on {FleetDirectConnector.Format(listen.Address, listen.Port)}: {exception.Message}").ConfigureAwait(false);
                        await node.StopAsync(hurry.Token).ConfigureAwait(false);
                        return NodeExitCodes.Failure;
                    }
                }

                using var fleetLifetime = new FleetLifetime(fleet);
                using var signals = new NodeSignals(stopRequested.Cancel, hurry.Cancel, static () => Environment.Exit(NodeExitCodes.Failure));
                Started?.Invoke(node, stopRequested.Cancel);
                await _output.WriteLineAsync($"CodeAlta Node runs on {profile.StateRoot}. Ctrl+C stops it.").ConfigureAwait(false);
                await _output.WriteLineAsync(fleet is null
                    ? "It listens for no device (--no-listen)."
                    : $"{name} ({fleet.DeviceId}) listens for paired devices on {fleet.EndPoint}; altanode pair makes a pairing link.").ConfigureAwait(false);
                try
                {
                    await Task.Delay(Timeout.Infinite, stopRequested.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                await _output.WriteLineAsync("Stopping CodeAlta Node...").ConfigureAwait(false);
                // The channels close first: no remote call reaches a runtime that stops.
                await fleetLifetime.StopAsync().ConfigureAwait(false);
                await node.StopAsync(hurry.Token).ConfigureAwait(false);
            }

            return NodeExitCodes.Success;
        }
        finally
        {
            if (ownsLogging) NodeLogging.Shutdown();
        }
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
