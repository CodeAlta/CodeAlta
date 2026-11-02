using System.Net;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>
/// The owner's local control of the node that runs on a profile (<see cref="NodeFleet"/>): where it listens, read from
/// <c>fleet/node-listener.json</c>, and a channel opened to it with the node's own device key, from this machine.
/// </summary>
internal sealed class NodeLocalControl
{
    private NodeLocalControl(CodeAltaInstanceProfile profile, CatalogOptions catalog, NodeProfileStatus status, NodeListenerFile? listener)
    {
        Profile = profile;
        Catalog = catalog;
        Status = status;
        Listener = listener;
    }

    /// <summary>Gets the profile.</summary>
    public CodeAltaInstanceProfile Profile { get; }

    /// <summary>Gets the catalog of the profile.</summary>
    public CatalogOptions Catalog { get; }

    /// <summary>Gets whether an instance holds the profile.</summary>
    public NodeProfileStatus Status { get; }

    /// <summary>Gets where the node of the profile listens, or null when no node that listens holds it.</summary>
    public NodeListenerFile? Listener { get; }

    /// <summary>Gets whether a node that listens runs on the profile: the local control reaches it.</summary>
    public bool NodeListens => Listener is not null;

    /// <summary>Reads the profile: whether a node holds it and listens.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The local control of the profile.</returns>
    public static NodeLocalControl Read(CodeAltaInstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var catalog = new CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot };
        var status = NodeProfileStatus.Read(profile);
        var listener = NodeFleet.ReadListenerFile(catalog);
        // A listener file a stopped node left, or one of another process, is no node to reach.
        if (!status.IsHeld || listener is null || listener.ProcessId != status.ProcessId) listener = null;
        return new NodeLocalControl(profile, catalog, status, listener);
    }

    /// <summary>Gets whether the profile has the device key of a node.</summary>
    public bool HasNodeKey => File.Exists(Path.Combine(Catalog.FleetRoot, DeviceIdentity.KeyFileName));

    /// <summary>Opens the owner's channel to the node, with its own key read from the profile.</summary>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The client and the key it holds; both are disposed by the caller.</returns>
    /// <exception cref="InvalidOperationException">No node that listens runs on the profile.</exception>
    /// <exception cref="IOException">The node did not answer.</exception>
    public async Task<(FleetNodeClient Client, DeviceIdentity Identity)> ConnectAsync(CancellationToken cancellationToken)
    {
        var listener = Listener ?? throw new InvalidOperationException("No node that listens runs on this profile.");
        var identity = DeviceIdentity.LoadOrCreate(Catalog);
        try
        {
            // This machine's own addresses first: the node takes its own key from this machine only.
            var addresses = listener.Addresses.OrderBy(static address => FleetDirectConnector.TrySplit(address, out var host, out _)
                && IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip) ? 0 : 1).ToArray();
            var client = await FleetNodeClient.ConnectAsOwnerAsync(addresses, identity, cancellationToken).ConfigureAwait(false);
            return (client, identity);
        }
        catch
        {
            identity.Dispose();
            throw;
        }
    }
}
