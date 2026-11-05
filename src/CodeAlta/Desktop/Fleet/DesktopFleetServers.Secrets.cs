using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

/// <summary>The secrets, variables, environments and approvals of a server as this computer reads them, with the names only it can give.</summary>
/// <param name="Secrets">The values of the person.</param>
/// <param name="Environments">The environments of the person, each with its definition when it verifies.</param>
/// <param name="Approvals">The approvals of the person's environments and of those the person approves.</param>
/// <param name="Devices">The devices of the organization, by id.</param>
/// <param name="People">The people of the organization.</param>
/// <param name="PersonId">The person of this computer.</param>
/// <param name="Machines">The machines of the person in the device list this computer verified.</param>
/// <param name="ScopeNames">The names this computer gives the opaque ids of spaces and projects.</param>
/// <param name="Manager">What reads variables back and acts on the values.</param>
internal sealed record DesktopFleetSecrets(IReadOnlyList<FleetSecretDto> Secrets, IReadOnlyList<(FleetEnvironmentDto Environment, FleetEnvironmentDefinition? Definition)> Environments,
    IReadOnlyList<FleetApprovalDto> Approvals, IReadOnlyDictionary<string, DeviceDto> Devices, IReadOnlyList<PersonDto> People, string PersonId,
    IReadOnlyList<FleetDeviceListMember> Machines, IReadOnlyDictionary<string, string> ScopeNames, FleetSecretsManager Manager);

// The secrets, variables and environments of a server (doc/fleet/secrets.md): this computer seals them for the machines of
// its person, from the device list as the anchor of the link verified it, and names the opaque ids of spaces and projects.
internal sealed partial class DesktopFleetServers
{
    /// <summary>Gets what manages the values of this computer's person at a server.</summary>
    /// <param name="linkId">The link.</param>
    /// <returns>The manager, over the anchored device list of the link.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    public FleetSecretsManager SecretsOf(string linkId)
    {
        var link = _store.List().FirstOrDefault(candidate => candidate.Id == linkId) ?? throw new KeyNotFoundException($"No server link has the id '{linkId}'.");
        var connector = Volatile.Read(ref _connections)?.Find(linkId)?.Connector;
        var devices = connector?.Devices is { } running && running.ServerAddress == link.Address && Handler is null
            ? running
            : new FleetOrganizationDevices(link.Address, Identity(), Path.Combine(_store.LinkFolder(link.Id), FleetOrganizationDevices.PinFileName), Handler)
            {
                CertificatePin = link.CertificatePin,
            };
        // The machines the signed access document gives this person are theirs to seal values for, those it gives another person no more.
        var access = connector?.Access ?? new FleetOrganizationAccess(devices, Path.Combine(_store.LinkFolder(link.Id), FleetOrganizationAccess.PinFileName));
        // What this computer saw of the values it manages there, kept with the link: a server that answers an older record seals
        // nothing for a machine this computer withdrew.
        return new FleetSecretsManager(devices)
        {
            Access = () => access.Current,
            Checkpoint = new FleetSecretControllerCheckpoint(Path.Combine(_store.LinkFolder(link.Id), FleetSecretControllerCheckpoint.FileName)),
        };
    }

    /// <summary>Reads the values, environments and approvals of this computer's person at a server.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>What the server holds, named.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DesktopFleetSecrets> ReadSecretsAsync(string linkId, CancellationToken cancellationToken)
    {
        var manager = SecretsOf(linkId);
        var list = await manager.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var client = Client(linkId);
        var secrets = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
        var environments = await manager.ListEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
        var approvals = await manager.ListApprovalsAsync(cancellationToken).ConfigureAwait(false);
        var devices = (await client.ListDevicesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static device => device.Id, StringComparer.Ordinal);
        var organization = await client.GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        var person = list.Members.TryGetValue(client.DeviceId, out var self) && self.OwnerId is { } owner ? owner : string.Empty;
        var access = manager.Access?.Invoke();
        var machines = list.Members.Values.Where(member => FleetAccessState.HoldsValuesOf(access, member, person) && member.Kind == FleetDeviceKinds.Node && !list.Removed.Contains(member.DeviceId))
            .OrderBy(member => devices.GetValueOrDefault(member.DeviceId)?.Name ?? member.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();
        return new DesktopFleetSecrets(secrets.Secrets, environments, approvals.Approvals, devices, organization.People, person, machines,
            await ScopeNamesAsync(linkId, cancellationToken).ConfigureAwait(false), manager);
    }

    /// <summary>
    /// Gives the scope a value of this computer's person is set in at a server: a space by the opaque id the server knows it
    /// by, a project by the blind index of its repository with the key of its space, which the person's machines compute too.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="kind">The kind of the scope (<see cref="FleetSecretScopeKinds"/>).</param>
    /// <param name="id">The id on this computer: the space, the project, the environment (its id at the server); null for the person.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The scope as the server knows it.</returns>
    /// <exception cref="ArgumentException">The scope cannot be given: a space not linked to the server, a project without a repository the person's machines recognize, an unknown kind.</exception>
    public async Task<FleetSecretScopeDto> ScopeAsync(string linkId, string kind, string? id, CancellationToken cancellationToken)
    {
        var keys = Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.MetadataKeys;
        switch (kind)
        {
            case FleetSecretScopeKinds.Person:
                return new FleetSecretScopeDto(kind);
            case FleetSecretScopeKinds.Environment when !string.IsNullOrWhiteSpace(id):
                return new FleetSecretScopeDto(kind, id);
            case FleetSecretScopeKinds.Space when !string.IsNullOrWhiteSpace(id):
                return keys?.CurrentFor(id)?.Grant?.SpaceId is { } spaceId
                    ? new FleetSecretScopeDto(kind, spaceId)
                    : throw new ArgumentException("The space is not linked to this server yet: link it first, its key reaches this computer then.");
            case FleetSecretScopeKinds.Project when !string.IsNullOrWhiteSpace(id):
                var project = await _host.ProjectCatalog.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ArgumentException("No project has this id.");
                var space = SpaceCatalog.ResolveSessionSpace(null, project, await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false));
                var key = keys?.CurrentFor(space) ?? throw new ArgumentException("The space of the project is not linked to this server yet.");
                var identity = await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false)
                    ?? throw new ArgumentException("The project has no identity the person's machines recognize.");
                return new FleetSecretScopeDto(kind, FleetProjectKeys.Of(key, identity));
            default:
                throw new ArgumentException("A value is scoped to the person, a space, a project or an environment.");
        }
    }

    // The spaces linked to the server by their opaque id, and the projects of this computer by their blind index with every key it holds.
    private async Task<IReadOnlyDictionary<string, string>> ScopeNamesAsync(string linkId, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Volatile.Read(ref _connections)?.Find(linkId)?.Connector?.MetadataKeys is not { } keys) return names;
        var spaces = await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (local, key) in keys.Current)
        {
            if (key.Grant?.SpaceId is { } spaceId) names.TryAdd(spaceId, spaces.FirstOrDefault(space => space.Id == local)?.Name ?? local);
        }

        if (keys.Held is { Count: > 0 } held)
        {
            foreach (var project in await _host.ProjectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await FleetProjectKeys.IdentityAsync(project, _repositories, cancellationToken).ConfigureAwait(false) is not { } identity) continue;
                foreach (var key in held) names.TryAdd(FleetProjectKeys.Of(key, identity), string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName);
            }
        }

        return names;
    }
}
