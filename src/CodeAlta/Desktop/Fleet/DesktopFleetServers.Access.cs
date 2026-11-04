using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

/// <summary>People and access at a server, as this computer reads them.</summary>
/// <param name="Self">What this computer's person is and may do.</param>
/// <param name="People">The people of the organization.</param>
/// <param name="Teams">The teams.</param>
/// <param name="Identities">The service identities.</param>
/// <param name="Grants">The grants this person may see.</param>
/// <param name="Owners">The resources this person may see, with their owners.</param>
/// <param name="Transfers">The transfers that concern this person.</param>
/// <param name="Proposals">The changes agents proposed.</param>
/// <param name="Devices">The devices of the organization, by id, for their names.</param>
internal sealed record DesktopFleetAccess(AccessSelfDto Self, IReadOnlyList<PersonAccessDto> People, IReadOnlyList<TeamDto> Teams, IReadOnlyList<ServiceIdentityDto> Identities,
    IReadOnlyList<GrantDto> Grants, IReadOnlyList<ResourceOwnerDto> Owners, IReadOnlyList<TransferDto> Transfers, IReadOnlyList<AccessProposalDto> Proposals,
    IReadOnlyDictionary<string, DeviceDto> Devices);

// People and access of a server (doc/fleet/access-control.md): reads, changes made by this computer's person, and the signed
// access document, which this computer signs with its key when its person is a signer and says so.
internal sealed partial class DesktopFleetServers
{
    /// <summary>Gets the access document of a server as this computer verifies it: the one of the connector of the link while it runs.</summary>
    /// <param name="linkId">The link.</param>
    /// <returns>The anchored access document of the link.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    public FleetOrganizationAccess AccessOf(string linkId)
    {
        var link = _store.List().FirstOrDefault(candidate => candidate.Id == linkId) ?? throw new KeyNotFoundException($"No server link has the id '{linkId}'.");
        if (Volatile.Read(ref _connections)?.Find(linkId)?.Connector is { } running && running.ServerAddress == link.Address && Handler is null) return running.Access;
        return new FleetOrganizationAccess(SecretsOf(linkId).Devices, Path.Combine(_store.LinkFolder(link.Id), FleetOrganizationAccess.PinFileName));
    }

    /// <summary>Reads people and access at a server, as far as this computer's person may see them.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>What the server holds.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DesktopFleetAccess> ReadAccessAsync(string linkId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        var self = await client.GetAccessSelfAsync(cancellationToken).ConfigureAwait(false);
        var people = await client.ListPeopleAsync(cancellationToken).ConfigureAwait(false);
        var teams = await client.ListTeamsAsync(cancellationToken).ConfigureAwait(false);
        var identities = await client.ListServiceIdentitiesAsync(cancellationToken).ConfigureAwait(false);
        var grants = await client.ListGrantsAsync(cancellationToken).ConfigureAwait(false);
        var owners = await client.ListOwnersAsync(cancellationToken).ConfigureAwait(false);
        var transfers = await client.ListTransfersAsync(cancellationToken).ConfigureAwait(false);
        var proposals = await client.ListAccessProposalsAsync(cancellationToken).ConfigureAwait(false);
        var devices = (await client.ListDevicesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static device => device.Id, StringComparer.Ordinal);
        return new DesktopFleetAccess(self, people.Items, teams.Items, identities.Items, grants.Items, owners.Items, transfers.Items, proposals.Items, devices);
    }

    /// <summary>Calls the access endpoints of a server as this computer's person.</summary>
    /// <typeparam name="T">The answer.</typeparam>
    /// <param name="linkId">The link.</param>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<T> AccessCallAsync<T>(string linkId, Func<FleetServerClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var client = Client(linkId);
        return await call(client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deprovisions a person who leaves, then signs their devices out of the device list with this computer's key: the server
    /// refuses them already, and the machines of the organization once they read the list. The signed access document that
    /// stops their work on team machines is signed next, by a signer who reviews it.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="personId">The person.</param>
    /// <param name="request">The successor of what they owned.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>What was done, and the devices that could not be signed out (the server refused or did not answer).</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused the deprovisioning.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<(DeprovisionResultDto Result, IReadOnlyList<string> NotRevoked)> DeprovisionAsync(string linkId, string personId, DeprovisionRequest request,
        CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        var result = await client.DeprovisionPersonAsync(personId, request, cancellationToken).ConfigureAwait(false);
        var left = new List<string>();
        foreach (var deviceId in result.DevicesToRevoke)
        {
            try
            {
                await client.RevokeAsync(deviceId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FleetServerException or HttpRequestException or FleetDeviceListException or IOException)
            {
                left.Add(deviceId);
            }
        }

        OnChanged();
        return (result, left);
    }

    /// <summary>Prepares the next version of the access document for this computer to sign, with what it changes as this computer reads it.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The version, or null when nothing waits.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetAccessDocumentException">The chain or the draft does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused (this person is no signer).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetAccessDraft?> PrepareAccessDocumentAsync(string linkId, CancellationToken cancellationToken) => AccessOf(linkId).PrepareAsync(cancellationToken);

    /// <summary>Signs a version of the access document this computer prepared.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="draft">The version.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The verified document with the new version.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused it (access changed meanwhile).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetAccessState> SignAccessDocumentAsync(string linkId, FleetAccessDraft draft, CancellationToken cancellationToken)
    {
        var state = await AccessOf(linkId).SignAsync(draft, cancellationToken).ConfigureAwait(false);
        OnChanged();
        return state;
    }
}
