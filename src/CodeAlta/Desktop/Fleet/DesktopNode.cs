using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Desktop.Mcp;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Hosting;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Fleet;

/// <summary>A pairing link this computer made for another computer of the person.</summary>
/// <param name="Link">The <c>codealta-pair://</c> link, which the other computer pastes.</param>
/// <param name="Words">The six words of the key of this computer, which the other computer shows too.</param>
/// <param name="ExpiresAt">When the link expires.</param>
/// <param name="Addresses">Where the link says this computer listens.</param>
internal sealed record DesktopNodePairing(string Link, string Words, DateTimeOffset ExpiresAt, IReadOnlyList<string> Addresses);

/// <summary>
/// This computer as a machine of the person's other computers (<c>doc/fleet/node.md</c>, Desktop and TUI as nodes): while the
/// person lets them pair with it, CodeAlta Desktop serves the node API over its own runtime, as <c>altanode</c> does, on a
/// port of its own (<see cref="DefaultPort"/>, so that a node and a server of the same machine keep 2585 and 2586). Another CodeAlta Desktop pairs
/// with it from a link made here, exactly as with a node: it then sees, drives and starts its sessions, and moves sessions
/// here when this computer lets it. On a computer of a Tailscale network, its name and address there come first in its links:
/// a laptop away from home reaches it through the tailnet.
/// </summary>
/// <remarks>
/// <para>
/// While the person lets their other computers reach it through the servers this computer is linked to
/// (<see cref="ReachableThroughServers"/>), the same node API serves the channels those servers relay to it: the person's other
/// devices of the organization list it with their machines, as a node, and reach it from anywhere without a port open here.
/// Each way is off until the person turns it on: nothing listens, nothing is relayed and no key is made before (S1).
/// </para>
/// <para>
/// The devices paired with this computer are kept in the fleet folder of its state root, beside the machines it paired
/// with: the same key serves both ways. Its changes are made one at a time.
/// </para>
/// </remarks>
internal sealed class DesktopNode : IAsyncDisposable
{
    /// <summary>
    /// The port a desktop listens on by default: beside those of a node (2585) and of CodeAlta Server (2586), so that a desktop that
    /// runs either on the same computer still listens.
    /// </summary>
    public const int DefaultPort = FleetChannelProtocol.DefaultPort + 2;

    /// <summary>How long a pairing link made here stays usable.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(10);

    private const string SettingsFileName = "desktop-node.json";
    // The name of a computer whose own name is not a valid device name.
    private const string FallbackName = "CodeAlta Desktop";

    private readonly CodeAltaHost _host;
    private readonly string _fleetRoot;
    private readonly string _settingsPath;
    private readonly string _version;
    private readonly TrustedDeviceStore _trusted;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private DesktopNodeSettings _settings;
    private DeviceIdentity? _identity;
    private FleetNodeService? _service;
    private FleetDirectListener? _listener;
    // Ends the relayed channels when the person no longer lets the servers reach this computer; null while they do not.
    private CancellationTokenSource? _relays;
    private IReadOnlyList<string> _addresses = [];
    private string? _problem;
    private string? _tailnet;
    private string? _network;
    private bool _disposed;

    /// <summary>Creates the node of this computer; it listens once <see cref="StartAsync"/> finds it turned on.</summary>
    /// <param name="host">The runtime of this computer, whose sessions the paired computers see.</param>
    /// <param name="machineName">The name of this computer, as the paired computers list it.</param>
    /// <param name="version">The version of CodeAlta.</param>
    /// <param name="fleetRoot">The fleet folder of the state root, where its setting, its key and its devices are kept.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DesktopNode(CodeAltaHost host, string machineName, string version, string fleetRoot)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(machineName);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        _host = host;
        _version = version;
        _fleetRoot = fleetRoot;
        _settingsPath = Path.Combine(fleetRoot, SettingsFileName);
        _trusted = new TrustedDeviceStore(fleetRoot, TimeProvider.System);
        _settings = ReadSettings(_settingsPath);
        Name = NameOf(machineName);
    }

    /// <summary>Gets the name the paired computers list this computer under.</summary>
    public string Name { get; }

    /// <summary>Gets the address the listener binds: every network of this computer, so that the others reach it.</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    /// <summary>Gets the port the listener binds in the place of the setting, for tests (0 picks a free one); null for the setting.</summary>
    public int? PortOverride { get; init; }

    /// <summary>
    /// Gets what finds this computer on its Tailscale network (<see cref="DesktopTailnet.FindAsync"/>), whose name and addresses
    /// come first in the links made here; null for none.
    /// </summary>
    public Func<CancellationToken, Task<DesktopTailnetNode?>>? FindTailnet { get; init; }

    /// <summary>Gets the name of this computer on its private network (its tailnet) while it listens there; null otherwise.</summary>
    public string? TailnetName => Volatile.Read(ref _tailnet);

    /// <summary>Gets the name of the private network <see cref="TailnetName"/> is on, such as <c>Tailscale</c>; null otherwise.</summary>
    public string? NetworkName => Volatile.Read(ref _network);

    /// <summary>
    /// Gets the policies of the servers of this computer, as it verified them: the work their relays or the paired computers start
    /// here follows them, and a stop of the kill switch ends it. Null for none.
    /// </summary>
    public CodeAlta.Fleet.Server.FleetPolicyGuard? Policy { get; init; }

    /// <summary>Gets the names of the servers this computer is linked to, whose relay can reach it; null for none.</summary>
    public Func<IReadOnlyList<string>>? Servers { get; init; }

    /// <summary>Gets whether the person lets their other computers pair with this one (off until they turn it on).</summary>
    public bool Visible => Volatile.Read(ref _settings).Visible;

    /// <summary>
    /// Gets whether the person lets their other computers reach this one through the servers it is linked to (off until they
    /// turn it on): it then serves the channels those servers relay to it, and says so to them.
    /// </summary>
    public bool ReachableThroughServers => Volatile.Read(ref _settings).ReachableThroughServers;

    /// <summary>Gets whether this computer listens for its paired computers now.</summary>
    public bool Listening => Volatile.Read(ref _listener) is not null;

    /// <summary>Gets the port this computer listens on, or would listen on.</summary>
    public int Port => Volatile.Read(ref _listener)?.LocalEndPoint.Port ?? PortOverride ?? Volatile.Read(ref _settings).Port ?? DefaultPort;

    /// <summary>Gets where the other computers reach this one while it listens; empty otherwise.</summary>
    public IReadOnlyList<string> Addresses => Volatile.Read(ref _addresses);

    /// <summary>Gets why this computer does not listen though it is visible, in words; null otherwise.</summary>
    public string? Problem => Volatile.Read(ref _problem);

    /// <summary>Gets the six words of the key of this computer, once it has one.</summary>
    public string? Words => Volatile.Read(ref _identity)?.Fingerprint.ToShortWords();

    /// <summary>Raised when this computer starts or stops listening, a computer paired with it, or a device changed.</summary>
    public event Action? Changed;

    /// <summary>Listens, and serves the relays of its servers, as the person turned them on before.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes once it listens, or once it is known that it does not.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Visible && !ReachableThroughServers) return;
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_disposed) await ApplyCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _changes.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Lets the person's other computers pair with this one, or stops: this computer listens, or no longer does.</summary>
    /// <param name="visible">Whether the other computers may pair with it and reach it.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task that completes once it listens or stopped.</returns>
    /// <exception cref="IOException">The setting cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The setting cannot be written.</exception>
    /// <exception cref="ObjectDisposedException">The application is closing.</exception>
    public async Task SetVisibleAsync(bool visible, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var settings = Volatile.Read(ref _settings) with { Visible = visible };
            WriteSettings(_settingsPath, settings);
            Volatile.Write(ref _settings, settings);
            await ApplyCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _changes.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Lets the person's other computers reach this one through the servers it is linked to, or stops: it serves the channels
    /// those servers relay to it, and its inventory says so, so that the person's other devices list it with their machines.
    /// Turned off, the relayed channels close at once and new ones are declined.
    /// </summary>
    /// <param name="reachable">Whether the other computers may reach it through its servers.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task that completes once the change applies.</returns>
    /// <exception cref="IOException">The setting cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The setting cannot be written.</exception>
    /// <exception cref="ObjectDisposedException">The application is closing.</exception>
    public async Task SetReachableThroughServersAsync(bool reachable, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var settings = Volatile.Read(ref _settings) with { ReachableThroughServers = reachable };
            WriteSettings(_settingsPath, settings);
            Volatile.Write(ref _settings, settings);
            await ApplyCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _changes.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Serves a channel a server relays to this computer, from a device of its organization: while the person lets the servers
    /// reach it, with the members of the organization as the connector of that server verifies them (the person's own devices
    /// see, drive and start its sessions, as after a pairing); otherwise the relay is closed at once. Failures never escape.
    /// </summary>
    /// <param name="connector">The control connection of the server that relays.</param>
    /// <param name="stream">The stream of the relay; this computer owns it.</param>
    /// <param name="cancellationToken">Ends the channel.</param>
    /// <returns>A task that completes once the channel closed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task ServeRelayedAsync(CodeAlta.Fleet.Server.FleetConnector connector, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(stream);
        var service = Volatile.Read(ref _service);
        var relays = Volatile.Read(ref _relays);
        if (service is null || relays is null || !ReachableThroughServers)
        {
            Log(false, "A channel a server relays was declined: this computer is not reachable through its servers.");
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        CancellationTokenSource linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, relays.Token);
        }
        catch (ObjectDisposedException)
        {
            // Turned off meanwhile.
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        using (linked)
        {
            try
            {
                await service.ServeRelayedAsync(stream, connector, linked.Token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The node API stopped under the channel: the person turned everything off, or the application closes.
            }
        }
    }

    /// <summary>Applies the policies of the servers to the work they or the paired computers started here: a stop ends it.</summary>
    /// <param name="cancellationToken">Cancels before the next session is stopped.</param>
    /// <returns>A task that completes once applied; a failure is logged.</returns>
    public async Task ApplyPolicyAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _service) is not { } service) return;
        try
        {
            await service.ApplyPolicyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log(true, $"The policy could not be applied to the work of the fleet here: {exception.Message}");
        }
    }

    /// <summary>
    /// Makes a pairing link for another computer of the person, replacing the one that was open: it works once, for
    /// <see cref="LinkLifetime"/>, and gives the computer that redeems it what a pairing gives (view, drive, start).
    /// </summary>
    /// <returns>The link and the words of this computer.</returns>
    /// <exception cref="InvalidOperationException">This computer does not listen.</exception>
    public DesktopNodePairing CreatePairing()
    {
        // A link names where this computer listens: the node API alone, which its servers relay to, makes none.
        if (Volatile.Read(ref _listener) is null || Volatile.Read(ref _service) is not { } service)
            throw new InvalidOperationException("This computer does not listen: let your other computers pair with it first.");
        var (offer, outcome) = service.Pairing.CreateOffer(Name, Addresses, LinkLifetime);
        _ = outcome.ContinueWith(_ => Changed?.Invoke(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new DesktopNodePairing(offer.ToUri(), offer.Fingerprint.ToShortWords(), offer.ExpiresAt, offer.Addresses);
    }

    /// <summary>Gets the outcome of the pairing link that is open: redeemed, expired or replaced; null when none is open.</summary>
    /// <returns>The task of its outcome.</returns>
    public Task<FleetPairingOutcome>? CurrentPairing() => Volatile.Read(ref _service)?.Pairing.CurrentOutcome();

    /// <summary>Lists the computers paired with this one, revoked ones left out.</summary>
    /// <returns>The devices, in the order they paired.</returns>
    /// <exception cref="IOException">The devices cannot be read.</exception>
    /// <exception cref="InvalidDataException">The file of the devices is not valid.</exception>
    public IReadOnlyList<TrustedDevice> Devices() => File.Exists(Path.Combine(_fleetRoot, TrustedDeviceStore.FileName)) ? _trusted.List() : [];

    /// <summary>Gives a computer paired with this one a permission, or takes it away; it applies at its next call.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="allowed">Whether it has it.</param>
    /// <returns>Whether the device is paired with this computer.</returns>
    /// <exception cref="IOException">The devices cannot be written.</exception>
    public bool SetPermission(string deviceId, DevicePermissions permission, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (Devices().FirstOrDefault(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal)) is not { } device) return false;
        var changed = _trusted.SetPermissions(deviceId, allowed ? device.Permissions | permission : device.Permissions & ~permission) is not null;
        Changed?.Invoke();
        return changed;
    }

    /// <summary>Revokes a computer paired with this one: its key is refused from now on, and its channels close.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether it was paired and is now revoked.</returns>
    /// <exception cref="IOException">The devices cannot be written.</exception>
    public async Task<bool> RevokeAsync(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var revoked = Volatile.Read(ref _service) is { } service ? await service.RevokeAsync(deviceId).ConfigureAwait(false) : _trusted.Revoke(deviceId);
        Changed?.Invoke();
        return revoked;
    }

    /// <summary>Stops listening and closes the channels of the paired computers; the setting stays for the next start.</summary>
    /// <returns>A task that completes once they are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            StopRelays();
            await StopListeningCoreAsync().ConfigureAwait(false);
            await StopServiceCoreAsync().ConfigureAwait(false);
            Volatile.Read(ref _identity)?.Dispose();
        }
        finally
        {
            _changes.Release();
        }
    }

    // Under the gate: the node API runs while either way is on, the listener while the person lets their computers pair, and
    // the relayed channels are served while they let the servers reach this computer.
    private async Task ApplyCoreAsync()
    {
        var settings = Volatile.Read(ref _settings);
        if (!settings.ReachableThroughServers) StopRelays();
        if (!settings.Visible) await StopListeningCoreAsync().ConfigureAwait(false);
        if (!settings.Visible && !settings.ReachableThroughServers)
        {
            await StopServiceCoreAsync().ConfigureAwait(false);
            return;
        }

        var service = EnsureService();
        if (settings.ReachableThroughServers && _relays is null)
        {
            Volatile.Write(ref _relays, new CancellationTokenSource());
            Log(false, "This computer serves the channels its servers relay from its other computers.");
        }

        if (settings.Visible) Listen(service);
    }

    private FleetNodeService EnsureService()
    {
        if (_service is { } running) return running;
        // The key of this computer, the one it pairs with its machines with: made now when it never paired.
        var identity = _identity ??= DeviceIdentity.LoadOrCreate(_fleetRoot);
        var service = new FleetNodeService(new FleetNodeServiceOptions
        {
            Host = _host, Identity = identity, TrustedDevices = _trusted, Name = Name, Version = _version,
            PairingAddresses = () => Volatile.Read(ref _addresses),
        })
        {
            Policy = Policy,
        };
        Volatile.Write(ref _service, service);
        return service;
    }

    private void Listen(FleetNodeService service)
    {
        if (_listener is not null) return;
        Volatile.Write(ref _problem, null);
        var port = PortOverride ?? Volatile.Read(ref _settings).Port ?? DefaultPort;
        try
        {
            var listener = FleetDirectListener.Start(new IPEndPoint(ListenAddress, port), service.ServeAsync);
            Volatile.Write(ref _addresses, FleetListenAddresses.Describe(listener.LocalEndPoint));
            Volatile.Write(ref _listener, listener);
            Log(false, $"This computer listens for its paired computers on port {listener.LocalEndPoint.Port}.");
            // Found beside the start, which does not wait for Tailscale to answer.
            _ = AddTailnetAsync(listener);
        }
        catch (SocketException exception)
        {
            Volatile.Write(ref _problem, $"Port {port} cannot be opened: {exception.Message}");
            Log(true, $"This computer cannot listen for its paired computers on port {port}: {exception.Message}");
        }
    }

    // The relayed channels end now; the next ones are declined until the person turns it on again.
    private void StopRelays()
    {
        if (Interlocked.Exchange(ref _relays, null) is not { } relays) return;
        relays.Cancel();
        relays.Dispose();
        Log(false, "This computer no longer serves the channels its servers relay.");
    }

    private async Task StopListeningCoreAsync()
    {
        var listener = Volatile.Read(ref _listener);
        Volatile.Write(ref _listener, null);
        Volatile.Write(ref _addresses, []);
        Volatile.Write(ref _problem, null);
        Volatile.Write(ref _tailnet, null);
        Volatile.Write(ref _network, null);
        if (listener is null) return;
        await listener.DisposeAsync().ConfigureAwait(false);
        Log(false, "This computer no longer listens for its paired computers.");
    }

    private async Task StopServiceCoreAsync()
    {
        if (Interlocked.Exchange(ref _service, null) is { } service) await service.DisposeAsync().ConfigureAwait(false);
    }

    // The name and the addresses of this computer on its tailnet go first: they answer at home and away alike. The listener
    // binds every IPv4 network, so the IPv6 addresses of the tailnet are left out.
    private async Task AddTailnetAsync(FleetDirectListener listener)
    {
        if (FindTailnet is not { } find) return;
        DesktopTailnetNode? tailnet;
        try
        {
            tailnet = await find(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (tailnet is null || !ReferenceEquals(Volatile.Read(ref _listener), listener)) return;
        var port = listener.LocalEndPoint.Port;
        var first = new List<string>();
        if (!IPAddress.TryParse(tailnet.Name, out _)) first.Add($"{tailnet.Name}:{port}");
        first.AddRange(tailnet.Addresses.Where(address => address.AddressFamily == ListenAddress.AddressFamily || ListenAddress.Equals(IPAddress.IPv6Any))
            .Select(address => FleetDirectConnector.Format(address, port)));
        Volatile.Write(ref _addresses, [.. first.Concat(Volatile.Read(ref _addresses)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(PairingPayload.IsValidAddress).Take(PairingPayload.MaxAddresses)]);
        Volatile.Write(ref _network, tailnet.Network);
        Volatile.Write(ref _tailnet, tailnet.Name);
        Log(false, $"This computer is reached on {tailnet.Network} as {tailnet.Name}.");
        Changed?.Invoke();
    }

    private static string NameOf(string machineName)
    {
        try
        {
            return TrustedDeviceStore.NormalizeName(machineName);
        }
        catch (ArgumentException)
        {
            return FallbackName;
        }
    }

    private static DesktopNodeSettings ReadSettings(string path)
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllBytes(path), DesktopNodeJsonContext.Default.DesktopNodeSettings) is { } settings
                ? settings
                : new DesktopNodeSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopNodeSettings();
        }
    }

    private static void WriteSettings(string path, DesktopNodeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(settings, DesktopNodeJsonContext.Default.DesktopNodeSettings));
        File.Move(staging, path, overwrite: true);
    }

    private static void Log(bool warning, string message)
    {
        if (!LogManager.IsInitialized) return;
        var logger = LogManager.GetLogger("CodeAlta.Desktop.Fleet");
        if (warning) logger.Warn(message);
        else logger.Info(message);
    }
}

/// <summary>The setting of the node of this computer, in the fleet folder of its state root.</summary>
internal sealed record DesktopNodeSettings
{
    /// <summary>Gets whether the person lets their other computers pair with this one.</summary>
    public bool Visible { get; init; }

    /// <summary>Gets the port it listens on; null for <see cref="DesktopNode.DefaultPort"/>.</summary>
    public int? Port { get; init; }

    /// <summary>Gets whether the person lets their other computers reach this one through the servers it is linked to.</summary>
    public bool ReachableThroughServers { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DesktopNodeSettings))]
internal sealed partial class DesktopNodeJsonContext : JsonSerializerContext;
