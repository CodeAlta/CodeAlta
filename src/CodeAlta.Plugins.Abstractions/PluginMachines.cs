namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// The machines this person's computer reaches, never the machines those machines reach. Only a built-in plugin that
/// serves the person is given this service; it uses the same authenticated session hosts as the window.
/// </summary>
public interface IPluginMachineService
{
    /// <summary>Gets whether this host can reach other machines.</summary>
    bool HasMachines { get; }

    /// <summary>Gets this computer's fleet device id, when it already has one, to recognize a cross-machine parent as local.</summary>
    string? LocalDeviceId => null;

    /// <summary>Reads the local sessions this host has given away, with their recorded destinations; never guesses from a session id.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Relinquished local sessions only. An unknown destination has no machine id and must not be guessed by name.</returns>
    ValueTask<IReadOnlyDictionary<string, PluginSessionMove>> LocalSessionMovesAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyDictionary<string, PluginSessionMove>>(System.Collections.Frozen.FrozenDictionary<string, PluginSessionMove>.Empty);

    /// <summary>Lists machines, including their last known state while offline.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The other machines of this computer; never this computer itself.</returns>
    ValueTask<IReadOnlyList<PluginMachine>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the services of exactly one machine, not a guess from a session id.</summary>
    /// <param name="machineId">The machine's device id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its services; null for an unknown machine.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machineId"/> is null.</exception>
    ValueTask<PluginMachineServices?> GetAsync(string machineId, CancellationToken cancellationToken = default);

    /// <summary>Follows bounded, coalesced changes of listed sessions; a reconnection starts with a quiet baseline.</summary>
    /// <param name="cancellationToken">Ends the subscription.</param>
    /// <returns>Machine-qualified session states and connection changes.</returns>
    IAsyncEnumerable<PluginMachineChange> WatchAsync(CancellationToken cancellationToken = default);

    /// <summary>Follows the bounded display of the one session a phone has open.</summary>
    /// <param name="machineId">Its machine.</param>
    /// <param name="sessionId">Its session id on that machine.</param>
    /// <param name="cancellationToken">Ends the subscription.</param>
    /// <returns>Full replacements of the live line, not an unbounded stream of deltas.</returns>
    IAsyncEnumerable<PluginMachineDisplay> WatchSessionAsync(string machineId, string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>A machine as this computer last knew it.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">Its display name.</param>
/// <param name="State"><c>online</c>, <c>connecting</c>, <c>offline</c> or <c>unauthorized</c>.</param>
/// <param name="Path"><c>direct</c> or <c>relayed</c>; null while not connected.</param>
/// <param name="LastSeen">When it last answered.</param>
/// <param name="Permissions">What the machine gives this computer: view, drive, start.</param>
/// <param name="Features">The protocol features the machine announces.</param>
/// <param name="Waiting">How many of its listed sessions wait for the person.</param>
public sealed record PluginMachine(string Id, string Name, string State, string? Path, DateTimeOffset? LastSeen,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> Features, int Waiting);

/// <summary>The recorded destination of a local session this computer has relinquished.</summary>
/// <param name="MachineId">Its exact destination device id, or null when the old record does not identify it.</param>
/// <param name="Machine">Its display name, never an identity.</param>
public sealed record PluginSessionMove(string? MachineId, string? Machine);

/// <summary>
/// The services of one machine, with the same plugin contracts used locally. Its alta service only accepts the typed
/// reads and work commands of the node API: it never executes an arbitrary command line on that machine.
/// </summary>
/// <param name="Machine">The machine.</param>
/// <param name="Alta">Its allowlisted reads and admitted work commands.</param>
/// <param name="Asks">Its questions; reviews are marked as requiring a computer.</param>
/// <param name="Permissions">Its permission requests.</param>
public sealed record PluginMachineServices(PluginMachine Machine, IPluginAltaService Alta, IPluginAskService Asks, IPluginPermissionService Permissions);

/// <summary>A change of a listed session, or of its machine's connection.</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="SessionId">The session; null for a connection change.</param>
/// <param name="State">The session's working, waiting or idle state, or the machine's connection state.</param>
/// <param name="At">The session's last activity, or when the connection changed.</param>
/// <param name="SpaceId">The session's space; null when unknown.</param>
/// <param name="Baseline">Whether this is the initial state or a reconnection baseline, not a notification.</param>
public sealed record PluginMachineChange(string MachineId, string? SessionId, string State, DateTimeOffset At, string? SpaceId, bool Baseline);

/// <summary>The live line of a machine-qualified session, as a full replacement.</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Revision">The display revision.</param>
/// <param name="Running">Whether its turn runs.</param>
/// <param name="Kind">The kind of its line, such as assistant or writing.</param>
/// <param name="Line">Its latest short line.</param>
/// <param name="At">When this replacement was observed.</param>
public sealed record PluginMachineDisplay(string MachineId, string SessionId, long Revision, bool Running, string? Kind, string? Line, DateTimeOffset At);

/// <summary>The machine service of a host that can confine it to its own built-in plugins.</summary>
public interface IPluginMachineRuntimeService : IPluginMachineService
{
    /// <summary>Gets the service of one plugin; folder plugins never receive the person's remote capabilities.</summary>
    /// <param name="pluginRuntimeKey">Its runtime key.</param>
    /// <param name="builtIn">Whether it comes from the application's own build.</param>
    /// <returns>This service for an admitted built-in, otherwise the no-op service.</returns>
    IPluginMachineService ForPlugin(string pluginRuntimeKey, bool builtIn);
}

/// <summary>A host without other machines: nodes intentionally never act as gateways.</summary>
public sealed class NoopPluginMachineService : IPluginMachineService
{
    private NoopPluginMachineService() { }

    /// <summary>Gets the shared stateless service.</summary>
    public static NoopPluginMachineService Instance { get; } = new();

    /// <inheritdoc />
    public bool HasMachines => false;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginMachine>> ListAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<PluginMachine>>([]);

    /// <inheritdoc />
    public ValueTask<PluginMachineServices?> GetAsync(string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        return ValueTask.FromResult<PluginMachineServices?>(null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginMachineChange> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginMachineDisplay> WatchSessionAsync(string machineId, string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machineId);
        ArgumentNullException.ThrowIfNull(sessionId);
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
