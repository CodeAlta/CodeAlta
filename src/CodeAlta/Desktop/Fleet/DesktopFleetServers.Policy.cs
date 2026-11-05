using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

// The signed policy of a server and its kill switch (doc/fleet/security-and-privacy.md, policies and the kill switch): read and
// verified as this computer verifies it, and signed with its key when its person is a signer and says so.
internal sealed partial class DesktopFleetServers
{
    /// <summary>Gets the policy of a server as this computer verifies it: the one of the connector of the link while it runs.</summary>
    /// <param name="linkId">The link.</param>
    /// <returns>The anchored policy of the link.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    public FleetOrganizationPolicy PolicyOf(string linkId)
    {
        var link = _store.List().FirstOrDefault(candidate => candidate.Id == linkId) ?? throw new KeyNotFoundException($"No server link has the id '{linkId}'.");
        if (Volatile.Read(ref _connections)?.Find(linkId)?.Connector is { } running && running.ServerAddress == link.Address && Handler is null) return running.Policy;
        return new FleetOrganizationPolicy(AccessOf(linkId), Path.Combine(_store.LinkFolder(link.Id), FleetOrganizationPolicy.PinFileName));
    }
}
