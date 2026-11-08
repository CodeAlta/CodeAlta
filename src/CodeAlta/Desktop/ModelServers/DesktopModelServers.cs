using System.Collections.Concurrent;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.ModelServers;

namespace CodeAlta.Desktop.ModelServers;

/// <summary>A machine whose model servers the window shows.</summary>
/// <param name="Id">Its id: <c>local</c> for this computer, the device id of a paired machine.</param>
/// <param name="Name">Its name.</param>
/// <param name="Local">Whether it is this computer.</param>
/// <param name="State"><c>online</c>, <c>offline</c>, <c>connecting</c>...</param>
/// <param name="Manages">Whether this computer may change its model servers.</param>
/// <param name="Uses">Whether this computer may use its model servers.</param>
/// <param name="Problem">Why it cannot be reached, or null.</param>
internal sealed record ModelServersMachine(string Id, string Name, bool Local, string State, bool Manages, bool Uses, string? Problem);

/// <summary>
/// The model servers of the window (<c>doc/model-servers.md</c>): those of this computer, in a model host of its own, and
/// those of the paired machines, read and changed through their node API. Changes come as events, per machine; the
/// providers of this computer reach the servers through <see cref="Transports"/>.
/// </summary>
internal sealed class DesktopModelServers : IAsyncDisposable
{
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(8);
    private readonly DesktopFleet? _fleet;
    private readonly ConcurrentDictionary<string, (FleetNodeClient Client, IDisposable Watch)> _watches = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposed = new();

    /// <summary>Creates the model servers of the window.</summary>
    /// <param name="local">The model host of this computer, or null for an instance that runs none (explicit roots).</param>
    /// <param name="fleet">The paired machines, or null.</param>
    /// <param name="transports">Where the providers reach the servers.</param>
    public DesktopModelServers(ModelHost? local, DesktopFleet? fleet, FleetModelServerTransports transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        Local = local;
        _fleet = fleet;
        Transports = transports;
        transports.Local = local;
        transports.Remote = machine => _fleet?.Find(machine) is { } host ? token => WaitOnlineAsync(host, token) : null;
        if (local is not null)
        {
            local.Changed += () => Changed?.Invoke(FleetModelServerTransports.LocalMachine);
            local.DownloadEnded += download => DownloadEnded?.Invoke(FleetModelServerTransports.LocalMachine, download);
            local.TaskEnded += task => TaskEnded?.Invoke(FleetModelServerTransports.LocalMachine, task);
        }
    }

    /// <summary>Gets the model host of this computer, or null.</summary>
    public ModelHost? Local { get; }

    /// <summary>Gets where the providers reach the servers.</summary>
    public FleetModelServerTransports Transports { get; }

    /// <summary>Raised with the id of a machine whose model servers changed.</summary>
    public event Action<string>? Changed;

    /// <summary>Raised when a download of this computer ended.</summary>
    public event Action<string, DownloadInfo>? DownloadEnded;

    /// <summary>Raised when an install or an update of this computer ended.</summary>
    public event Action<string, HostTask>? TaskEnded;

    /// <summary>Gets or sets what adds or changes a provider of this computer that uses a model server.</summary>
    public Func<string, string, string, CancellationToken, Task>? SaveLocalProvider { get; set; }

    /// <summary>Starts the model host of this computer.</summary>
    /// <param name="cancellationToken">Ends the start.</param>
    /// <returns>A task that ends once it runs.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Local is not null) await Local.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the machines: this computer first, then the paired machines.</summary>
    /// <returns>The machines.</returns>
    public IReadOnlyList<ModelServersMachine> Machines()
    {
        var machines = new List<ModelServersMachine>();
        if (Local is not null) machines.Add(new ModelServersMachine(FleetModelServerTransports.LocalMachine, Environment.MachineName, true, "online", true, true, null));
        foreach (var host in _fleet?.Hosts ?? [])
        {
            if (host.MachineId is not { } id || !host.IsPaired) continue;
            var permissions = host.Listing.Info?.Permissions ?? host.Machine.Permissions;
            var manages = permissions.Contains("manage", StringComparer.Ordinal);
            machines.Add(new ModelServersMachine(id, host.Listing.Info?.Name ?? host.Machine.Name, false, RemoteSessionHost.Describe(host.State) is var state && host.State == RemoteMachineState.Online ? "online" : state,
                manages, manages || permissions.Contains("models", StringComparer.Ordinal), host.Problem));
        }

        return machines;
    }

    /// <summary>Reads the model servers of a machine.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>Its status.</returns>
    /// <exception cref="ModelHostException">The machine is not known, cannot be reached or runs no model servers.</exception>
    public async Task<ModelHostStatus> StatusAsync(string machine, CancellationToken cancellationToken)
    {
        if (machine == FleetModelServerTransports.LocalMachine)
            return (Local ?? throw new ModelHostException("unavailable", "This instance runs no model server.")).GetStatus();
        var host = Remote(machine);
        return await CallAsync(host, static (client, token) => client.GetModelServersAsync(token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks a machine for an action on its model servers.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="action">The action.</param>
    /// <param name="requestedBy">Who asks, for the records of this computer.</param>
    /// <param name="cancellationToken">Ends the action.</param>
    /// <returns>What it gave.</returns>
    /// <exception cref="ModelHostException">It is refused or fails.</exception>
    public async Task<ModelHostActionResult> DoAsync(string machine, ModelHostAction action, string? requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (machine == FleetModelServerTransports.LocalMachine)
            return await ModelHostActions.RunAsync(Local ?? throw new ModelHostException("unavailable", "This instance runs no model server."), action, requestedBy, cancellationToken)
                .ConfigureAwait(false);
        var host = Remote(machine);
        return await CallAsync(host, (client, token) => client.DoModelServersAsync(action, token), cancellationToken, managed: true).ConfigureAwait(false);
    }

    /// <summary>Follows the changes of the paired machines that are online, once each.</summary>
    /// <param name="cancellationToken">Ends the subscriptions.</param>
    /// <returns>A task that ends once they are asked for.</returns>
    public async Task WatchMachinesAsync(CancellationToken cancellationToken)
    {
        foreach (var host in _fleet?.Hosts ?? [])
        {
            if (host.MachineId is not { } id || host.State != RemoteMachineState.Online || host.CurrentClient is not { } client) continue;
            if (_watches.TryGetValue(id, out var existing) && ReferenceEquals(existing.Client, client)) continue;
            try
            {
                var watch = await client.WatchModelServersAsync(_ => Changed?.Invoke(id), cancellationToken).ConfigureAwait(false);
                if (_watches.TryRemove(id, out var replaced)) replaced.Watch.Dispose();
                _watches[id] = (client, watch);
            }
            catch (Exception exception) when (exception is FleetException or IOException or TimeoutException or ObjectDisposedException or FleetConnectionClosedException)
            {
                // A machine of an older version, or one that does not run model servers: it is shown without live changes.
            }
        }
    }

    /// <summary>Adds a provider for a server on each machine named, so that the sessions there use it.</summary>
    /// <param name="serverMachine">The machine of the server.</param>
    /// <param name="serverId">The server.</param>
    /// <param name="name">The name of the provider.</param>
    /// <param name="machines">The machines whose sessions use it.</param>
    /// <param name="cancellationToken">Ends the changes.</param>
    /// <returns>For each machine, null when done or why not.</returns>
    public async Task<IReadOnlyDictionary<string, string?>> UseInSessionsAsync(string serverMachine, string serverId, string name, IReadOnlyList<string> machines,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        var key = ProviderKey(serverMachine, serverId);
        foreach (var machine in machines.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (machine == FleetModelServerTransports.LocalMachine)
                {
                    // This computer names the machine of the server, or itself.
                    var reference = (serverMachine == FleetModelServerTransports.LocalMachine ? FleetModelServerTransports.LocalMachine : serverMachine) + "/" + serverId;
                    if (SaveLocalProvider is null) throw new ModelHostException("unavailable", "This instance cannot add providers.");
                    await SaveLocalProvider(key, name, reference, cancellationToken).ConfigureAwait(false);
                }
                else if (machine == serverMachine)
                {
                    var host = Remote(machine);
                    await CallAsync(host, (client, token) => client.SetProviderAsync(new FleetProviderSetParams(key, "llama-cpp", name, null, null, null) { Server = "local/" + serverId }, token),
                        cancellationToken, managed: true).ConfigureAwait(false);
                }
                else
                {
                    results[machine] = "A machine uses the model servers of this computer or of itself only.";
                    continue;
                }

                results[machine] = null;
            }
            catch (ModelHostException exception)
            {
                results[machine] = exception.Message;
            }
        }

        return results;
    }

    /// <summary>The key of the provider of a server: <c>llama-&lt;server&gt;</c>, with the machine for one of another machine.</summary>
    /// <param name="machine">The machine of the server.</param>
    /// <param name="server">The server.</param>
    /// <returns>The key.</returns>
    internal string ProviderKey(string machine, string server)
    {
        if (machine == FleetModelServerTransports.LocalMachine) return "llama-" + server;
        var name = _fleet?.Find(machine) is { } host ? host.Listing.Info?.Name ?? host.Machine.Name : machine[..Math.Min(8, machine.Length)];
        var slug = new string(name.ToLowerInvariant().Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return $"llama-{slug}-{server}";
    }

    private RemoteSessionHost Remote(string machine)
        => _fleet?.Find(machine) ?? throw new ModelHostException("not_found", "This computer is not paired with that machine.");

    private static async Task<FleetNodeClient> WaitOnlineAsync(RemoteSessionHost host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RemoteTimeout);
        try
        {
            return await host.WaitOnlineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"{host.Machine.Name} is {RemoteSessionHost.Describe(host.State)}.");
        }
    }

    private static async Task<T> CallAsync<T>(RemoteSessionHost host, Func<FleetNodeClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken, bool managed = false)
    {
        try
        {
            if (managed) return await host.ManageAsync(call, changes: false, cancellationToken).ConfigureAwait(false);
            var client = await WaitOnlineAsync(host, cancellationToken).ConfigureAwait(false);
            return await call(client, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new ModelHostException("offline", $"{host.Machine.Name} is {RemoteSessionHost.Describe(host.State)}.");
        }
        catch (FleetException exception) when (exception.Error.Code == FleetErrorCodes.MethodNotFound)
        {
            throw new ModelHostException("unsupported", $"{host.Machine.Name} runs a version of CodeAlta without model servers: update it.");
        }
        catch (FleetException exception)
        {
            throw new ModelHostException(exception.Error.Code == FleetModelServerMethods.NotFound ? "not_found" : exception.Error.Code, exception.Error.Message ?? exception.Error.Code);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or FleetConnectionClosedException)
        {
            throw new ModelHostException("offline", exception.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync().ConfigureAwait(false);
        foreach (var watch in _watches.Values) watch.Watch.Dispose();
        if (Local is not null) await Local.DisposeAsync().ConfigureAwait(false);
        _disposed.Dispose();
    }
}
