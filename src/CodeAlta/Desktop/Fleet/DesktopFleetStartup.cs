using CodeAlta.Desktop.SessionHosts;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// The fleet of a desktop as its start wires it (<c>doc/fleet/readme.md</c>): the paired machines and their moves, the
/// sessions agents start on them, and the CodeAlta Servers the instance is linked to. Staying local is the default: a
/// profile that never paired a machine nor joined a server makes no device key and reaches no network here
/// (<c>doc/fleet/security-and-privacy.md</c>, S1), until a pairing or a link needs one.
/// </summary>
internal sealed class DesktopFleetStartup
{
    private DesktopFleetStartup(DesktopFleet fleet, DesktopRemoteChildren remoteChildren, DesktopFleetServers servers, DesktopNode node)
    {
        Fleet = fleet;
        RemoteChildren = remoteChildren;
        Servers = servers;
        Node = node;
    }

    /// <summary>Gets the paired machines of the instance.</summary>
    public DesktopFleet Fleet { get; }

    /// <summary>Gets the sessions agents of this computer start on its machines.</summary>
    public DesktopRemoteChildren RemoteChildren { get; }

    /// <summary>Gets the CodeAlta Servers this instance is linked to.</summary>
    public DesktopFleetServers Servers { get; }

    /// <summary>Gets this computer as a machine of the person's other computers, while they let it be one.</summary>
    public DesktopNode Node { get; }

    /// <summary>Wires the fleet of a desktop instance; nothing starts yet.</summary>
    /// <param name="host">The host of the instance.</param>
    /// <param name="sessionHosts">The hosts of the sessions the windows show, which the fleet adds paired machines to.</param>
    /// <param name="machineName">The name of this computer.</param>
    /// <param name="version">The version of CodeAlta.</param>
    /// <param name="handler">The HTTP handler of the calls to servers, for tests; null for the usual one.</param>
    /// <param name="privateNetworks">
    /// The private networks of this computer, the first of which that finds it names it in the pairing links; null for Tailscale alone.
    /// </param>
    /// <param name="modelServers">The model host of this computer, which its node API offers its paired computers; null for none.</param>
    /// <returns>The fleet.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static DesktopFleetStartup Create(CodeAltaHost host, SessionHostRegistry sessionHosts, string machineName, string version, HttpMessageHandler? handler = null,
        CodeAlta.Plugins.PrivateNetworks.PrivateNetworkRegistry? privateNetworks = null, CodeAlta.ModelServers.ModelHost? modelServers = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(sessionHosts);
        ArgumentNullException.ThrowIfNull(machineName);
        ArgumentNullException.ThrowIfNull(version);
        var fleet = new DesktopFleet(host.CatalogOptions.FleetRoot, sessionHosts.TakeMembership(), machineName);
        fleet.Moves = new DesktopMoves(host, fleet, machineName, host.CatalogOptions.FleetRoot);
        // The sessions agents of this computer start on its machines, and the answers they send back.
        var remoteChildren = new DesktopRemoteChildren(host, fleet, machineName);
        host.RuntimeService.RemoteChildren = remoteChildren;
        // The CodeAlta Servers this instance is linked to: none until a server is created or joined.
        var servers = new DesktopFleetServers(host, machineName, version) { Handler = handler };
        // An agent of this computer starts no work elsewhere when a stop reaches it or its organization says so.
        remoteChildren.Policy = servers.Policy;
        // A machine of an organization this computer is not paired with is reached through the relay of its server.
        fleet.RelayRoute = servers.FindRoute;
        // The spaces its machines publish get a key from this computer, whether it links them or not: their sessions show their titles.
        servers.MachineSpaces = fleet.PublishedSpaces;
        servers.Changed += () => _ = fleet.SyncOrganizationMachinesAsync(servers.OrganizationMachines(), CancellationToken.None);
        // This computer as a machine of the person's other computers: it listens only once the person turned it on.
        // On a private network (Tailscale, or one a plugin brings), its links name it there first, so that a laptop away from home
        // still reaches it.
        // Once the person lets them, the person's other devices of the organizations of its servers reach it through their
        // relays too: the work they start here follows the policies of those servers.
        var node = new DesktopNode(host, machineName, version, host.CatalogOptions.FleetRoot)
        {
            ModelServers = modelServers,
            FindTailnet = privateNetworks is null ? Mcp.DesktopTailnet.FindAsync : async token =>
                await privateNetworks.FindThisComputerAsync(token).ConfigureAwait(false) is { } found
                    ? new Mcp.DesktopTailnetNode(found.Presence.Name, found.Presence.Addresses, found.Network.DisplayName)
                    : null,
            Policy = servers.Policy,
            // A computer that pairs with this one both ways is listed with its machines.
            PairedBack = fleet.AddPairedBackAsync,
            Servers =() => [.. servers.Connections.Where(static connection => connection.Link.State != CodeAlta.Fleet.Server.FleetServerLinkStates.Revoked)
                .Select(static connection => connection.Link.OrganizationName ?? connection.Link.Url)],
        };
        servers.AcceptRelay = node.ServeRelayedAsync;
        servers.Reachable = () => node.ReachableThroughServers;
        servers.PolicyChanged += () => _ = node.ApplyPolicyAsync(CancellationToken.None);
        // Turned on or off: the servers tell the person's other devices at once.
        node.Changed += servers.Republish;
        return new DesktopFleetStartup(fleet, remoteChildren, servers, node);
    }

    /// <summary>Starts the deliveries of the sessions agents started on other machines, once the window shows the application.</summary>
    public void StartRemoteChildren() => RemoteChildren.Start();

    /// <summary>
    /// Starts what the window waits for: the moves this computer took and a stop left unfinished end first, by its own
    /// decisions; then the paired machines are reached, this computer listens for the computers paired with it when the
    /// person lets them, and the servers this instance is linked to are reached.
    /// </summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes once they started.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Fleet.Moves is { } moves) await moves.RecoverAsync(cancellationToken).ConfigureAwait(false);
        await Fleet.StartAsync(cancellationToken).ConfigureAwait(false);
        await Node.StartAsync(cancellationToken).ConfigureAwait(false);
        await Servers.StartAsync(cancellationToken).ConfigureAwait(false);
    }
}
