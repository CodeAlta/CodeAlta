using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

// The owners of resources and their transfers (doc/fleet/access-control.md, resources and their owners, ownership over time).
internal sealed partial class AccessService
{
    /// <summary>
    /// Lists the resources and their owners: every one for those who manage or review access, those a person owns or whose
    /// team they are in for others. A resource whose owner is suspended says so, so that someone takes it.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The resources.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<AccessListDto<ResourceOwnerDto>> ListOwnersAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var all = AccessPolicy.Allows(snapshot, person.Id, "access.manage", AccessTarget.Organization, now)
            || AccessPolicy.Allows(snapshot, person.Id, "access.review", AccessTarget.Organization, now);
        var listed = new List<ResourceOwnerDto>();
        foreach (var resource in await ListResourcesAsync(actor.OrganizationId, snapshot, cancellationToken).ConfigureAwait(false))
        {
            var mine = resource.OwnerKind == FleetPrincipalKinds.Person && resource.OwnerId == person.Id
                || resource.OwnerKind == FleetPrincipalKinds.Team && snapshot.TeamRole(resource.OwnerId, person.Id) is not null;
            if (all || mine) listed.Add(resource);
        }

        return new(listed);
    }

    /// <summary>Offers a resource to another owner: a person, a team or the organization, who accepts it.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="request">The resource and its new owner.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The transfer, offered.</returns>
    /// <exception cref="CoordinatorException">The caller may not, the resource or the new owner is unknown, or a transfer of it is offered already.</exception>
    public async Task<TransferDto> OfferTransferAsync(DeviceRecord actor, OfferTransferRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request is null || !FleetResourceKinds.Transfers(request.ResourceKind)) throw Invalid("A machine or an automation is given to another owner.");
        var owner = await OwnerOfAsync(actor.OrganizationId, snapshot, request.ResourceKind, request.ResourceId, cancellationToken).ConfigureAwait(false)
            ?? throw NotFound("No resource of the organization has this id.");
        Require(snapshot, person, request.ResourceKind == FleetResourceKinds.Machine ? "machine.manage" : "automation.transfer",
            AccessTarget.Resource(request.ResourceKind, request.ResourceId, owner.Kind, owner.Id));
        var to = ResolveOwner(snapshot, actor.OrganizationId, request.ToKind, request.ToId);
        if (to.Kind == owner.Kind && to.Id == owner.Id) throw Invalid("The resource is theirs already.");
        var transfer = new TransferRecord(SetupService.NewId(), actor.OrganizationId, request.ResourceKind, request.ResourceId, owner.Kind, owner.Id, to.Kind, to.Id,
            FleetTransferStates.Offered, FleetTransferReasons.Transfer, person.Id, _time.GetUtcNow(), null, null) { HasHandover = request.Handover is not null };
        if (request.Handover is { } handover)
        {
            // What an automation does goes sealed from its owner's computer to the devices of the person who takes it, and to no other.
            if (request.ResourceKind != FleetResourceKinds.Automation || to.Kind != FleetPrincipalKinds.Person || owner.Kind != FleetPrincipalKinds.Person || owner.Id != person.Id)
                throw Invalid("A hand-over goes with an automation its owner gives to a person.");
            if (string.IsNullOrEmpty(handover.Sealed) || handover.Sealed.Length > MaxSealedLength || handover.Wraps is not { Count: > 0 and <= MaxWraps })
                throw Invalid("A hand-over is what the automation does, sealed, and a copy of its key for each device of who takes it.");
            var devices = (await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false))
                .Where(device => device.Kind == FleetDeviceKinds.Controller && device.Status == DeviceStatuses.Approved && device.OwnerPersonId == to.Id)
                .Select(static device => device.Id).ToHashSet(StringComparer.Ordinal);
            if (handover.Wraps.Any(wrap => wrap is null || !devices.Contains(wrap.DeviceId) || string.IsNullOrEmpty(wrap.Wrapped) || wrap.Wrapped.Length > MaxWrappedLength)
                || handover.Wraps.Select(static wrap => wrap.DeviceId).Distinct(StringComparer.Ordinal).Count() != handover.Wraps.Count)
                throw Invalid("A copy of the key of a hand-over goes once to each device of who takes it, and to no other.");
        }

        if (!await _access.OfferTransferAsync(transfer, request.Handover?.Sealed, [.. request.Handover?.Wraps.Select(static wrap => (wrap.DeviceId, wrap.Wrapped)) ?? []],
                origin.Audit("transfer.offer", actor, transfer.ResourceId, details: TransferDetails(transfer)), cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "A transfer of this resource is offered already: it is decided or cancelled first.");
        Changed(actor.OrganizationId);
        return ToDto(transfer, owner.Name);
    }

    /// <summary>Reads the hand-over of a transfer for the calling device: one of the person it is offered or given to.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="transferId">The transfer.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the automation does, sealed, and this device's copy of the key.</returns>
    /// <exception cref="CoordinatorException">No hand-over is there for this device.</exception>
    public async Task<TransferHandoverCopyDto> GetHandoverAsync(DeviceRecord actor, string transferId, CancellationToken cancellationToken)
    {
        var (_, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var transfer = await _access.GetTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
        if (transfer is null || transfer.OrganizationId != actor.OrganizationId || transfer.ToKind != FleetPrincipalKinds.Person || transfer.ToId != person.Id
            || transfer.State is not (FleetTransferStates.Offered or FleetTransferStates.Accepted)
            || await _access.GetTransferHandoverAsync(transferId, actor.Id, cancellationToken).ConfigureAwait(false) is not { } copy)
            throw NotFound("No hand-over of this transfer is there for this device.");
        return new TransferHandoverCopyDto(transfer.Id, transfer.ResourceId, transfer.FromId, copy.Sealed, copy.Wrapped);
    }

    /// <summary>Lists the transfers the caller may see: those that concern them or their teams, all for those who manage access.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The transfers, the newest first.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<AccessListDto<TransferDto>> ListTransfersAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var all = AccessPolicy.Allows(snapshot, person.Id, "access.manage", AccessTarget.Organization, now)
            || AccessPolicy.Allows(snapshot, person.Id, "access.review", AccessTarget.Organization, now);
        var transfers = await _access.ListTransfersAsync(actor.OrganizationId, MaxListed, cancellationToken).ConfigureAwait(false);
        var devices = (await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(static device => device.Id, StringComparer.Ordinal);
        return new([.. transfers.Where(transfer => all || transfer.OfferedByPersonId == person.Id || Concerns(snapshot, person.Id, transfer.FromKind, transfer.FromId)
                || Concerns(snapshot, person.Id, transfer.ToKind, transfer.ToId))
            .Select(transfer => ToDto(transfer, transfer.ResourceKind == FleetResourceKinds.Machine && devices.TryGetValue(transfer.ResourceId, out var device) ? device.Name : null))]);
    }

    /// <summary>
    /// Decides a transfer: the new owner accepts or declines it (a maintainer for a team, an owner or admin for the
    /// organization); the one who offered it, or whoever could, cancels it. An automation taken waits, paused, until its
    /// new owner resumes it.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="transferId">The transfer.</param>
    /// <param name="state">Accepted, declined or cancelled.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The transfer, decided.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the transfer is not offered any more.</exception>
    public async Task<TransferDto> DecideTransferAsync(DeviceRecord actor, string transferId, string state, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var transfer = await _access.GetTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
        if (transfer is null || transfer.OrganizationId != actor.OrganizationId) throw NotFound("No transfer has this id.");
        if (transfer.State != FleetTransferStates.Offered) throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The transfer was decided already.");
        var now = _time.GetUtcNow();
        if (state == FleetTransferStates.Cancelled)
        {
            if (transfer.OfferedByPersonId != person.Id)
            {
                var owner = await OwnerOfAsync(actor.OrganizationId, snapshot, transfer.ResourceKind, transfer.ResourceId, cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound("The resource is gone.");
                Require(snapshot, person, transfer.ResourceKind == FleetResourceKinds.Machine ? "machine.manage" : "automation.transfer",
                    AccessTarget.Resource(transfer.ResourceKind, transfer.ResourceId, owner.Kind, owner.Id));
            }
        }
        else if (!Accepts(snapshot, person, transfer.ToKind, transfer.ToId))
        {
            throw Denied("Only the new owner takes or refuses what is given to them: the person, a maintainer of the team, an owner or admin for the organization.");
        }

        ResourceOwnerRecord? next = null;
        if (state == FleetTransferStates.Accepted)
        {
            var held = snapshot.Owner(transfer.ResourceKind, transfer.ResourceId);
            next = new ResourceOwnerRecord(actor.OrganizationId, transfer.ResourceKind, transfer.ResourceId, transfer.ToKind, transfer.ToId, held?.AuthorPersonId,
                held?.AuthorSessionId, transfer.ResourceKind == FleetResourceKinds.Automation, now);
        }

        var action = state switch { FleetTransferStates.Accepted => "transfer.accept", FleetTransferStates.Declined => "transfer.decline", _ => "transfer.cancel" };
        var decided = await _access.DecideTransferAsync(transferId, state, person.Id, now, next, origin.Audit(action, actor, transfer.ResourceId, details: TransferDetails(transfer)),
            cancellationToken).ConfigureAwait(false) ?? throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The transfer was decided already.");
        Changed(actor.OrganizationId);
        return ToDto(decided, null);
    }

    /// <summary>Resumes a resource that waits for its owner (after a transfer, or its owner leaving); for its owner's side.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="resourceKind">The kind of resource.</param>
    /// <param name="resourceId">Its id.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The resource and its owner.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the resource does not wait.</exception>
    public async Task<ResourceOwnerDto> ResumeAsync(DeviceRecord actor, string resourceKind, string resourceId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var held = snapshot.Owner(resourceKind, resourceId);
        if (held is null || !held.Paused) throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The resource does not wait for its owner.");
        if (!Accepts(snapshot, person, held.OwnerKind, held.OwnerId)) throw Denied("Only its owner resumes it: the person, a maintainer of the team, an owner or admin for the organization.");
        var resumed = held with { Paused = false, UpdatedAt = _time.GetUtcNow() };
        await _access.PutResourceOwnerAsync(resumed, origin.Audit("owner.resume", actor, resourceId, details: Json(("resource", resourceKind + ":" + resourceId))), cancellationToken)
            .ConfigureAwait(false);
        Changed(actor.OrganizationId);
        var resources = await ListResourcesAsync(actor.OrganizationId, snapshot with { Owners = [.. snapshot.Owners.Where(owner => owner != held), resumed] }, cancellationToken)
            .ConfigureAwait(false);
        return resources.First(resource => resource.ResourceKind == resourceKind && resource.ResourceId == resourceId);
    }

    /// <summary>The owner of a resource now, with its name: the record of its own, or the one its record implies.</summary>
    internal async Task<(string Kind, string Id, string? Name)?> OwnerOfAsync(string organizationId, AccessSnapshot snapshot, string kind, string id,
        CancellationToken cancellationToken)
    {
        string? name = null;
        string? implied = null;
        switch (kind)
        {
            case FleetResourceKinds.Machine:
                var device = await _store.GetDeviceAsync(id, cancellationToken).ConfigureAwait(false);
                if (device is null || device.OrganizationId != organizationId || device.Status == DeviceStatuses.Revoked || device.Kind == FleetDeviceKinds.Recovery) return null;
                (name, implied) = (device.Name, device.OwnerPersonId);
                break;
            case FleetResourceKinds.Automation:
                foreach (var person in snapshot.People)
                {
                    var automations = await _automations.ListAutomationsAsync(organizationId, person.Id, cancellationToken).ConfigureAwait(false);
                    if (automations.Any(automation => automation.AutomationId == id && !automation.Removed))
                    {
                        implied = person.Id;
                        break;
                    }
                }

                if (implied is null && snapshot.Owner(kind, id) is null) return null;
                break;
            case FleetResourceKinds.Session:
                var sessions = await _store.ListSessionsAsync(organizationId, cancellationToken).ConfigureAwait(false);
                if (sessions.FirstOrDefault(session => session.Id == id) is not { } published) return null;
                var machine = await _store.GetDeviceAsync(published.MachineId, cancellationToken).ConfigureAwait(false);
                implied = machine?.OwnerPersonId;
                if (snapshot.Owner(FleetResourceKinds.Machine, published.MachineId) is { } machineOwner) return (machineOwner.OwnerKind, machineOwner.OwnerId, null);
                break;
            default:
                if (snapshot.Owner(kind, id) is null) return null;
                break;
        }

        if (snapshot.Owner(kind, id) is { } own) return (own.OwnerKind, own.OwnerId, name);
        return implied is null ? null : (FleetPrincipalKinds.Person, implied, name);
    }

    /// <summary>Every resource with an owner the server knows: the machines, the automations, and those with an owner record of their own.</summary>
    private async Task<List<ResourceOwnerDto>> ListResourcesAsync(string organizationId, AccessSnapshot snapshot, CancellationToken cancellationToken)
    {
        var resources = new Dictionary<(string, string), ResourceOwnerDto>();
        var devices = await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var machines = await _store.ListMachinesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var hosts = new HashSet<string>(machines.Select(static machine => machine.DeviceId), StringComparer.Ordinal);
        foreach (var device in devices.Where(device => device.Status == DeviceStatuses.Approved && (device.Kind == FleetDeviceKinds.Node || hosts.Contains(device.Id))))
            Add(FleetResourceKinds.Machine, device.Id, device.Name, device.OwnerPersonId, null);
        foreach (var person in snapshot.People)
        {
            foreach (var automation in await _automations.ListAutomationsAsync(organizationId, person.Id, cancellationToken).ConfigureAwait(false))
            {
                if (!automation.Removed) Add(FleetResourceKinds.Automation, automation.AutomationId, null, person.Id, person.Id);
            }
        }

        foreach (var owner in snapshot.Owners.Where(owner => !resources.ContainsKey((owner.ResourceKind, owner.ResourceId))))
            Add(owner.ResourceKind, owner.ResourceId, null, null, owner.AuthorPersonId);
        return [.. resources.Values.OrderBy(static resource => resource.ResourceKind, StringComparer.Ordinal).ThenBy(static resource => resource.Name ?? resource.ResourceId,
            StringComparer.OrdinalIgnoreCase)];

        void Add(string kind, string id, string? name, string? impliedPerson, string? author)
        {
            var own = snapshot.Owner(kind, id);
            var (ownerKind, ownerId) = own is not null ? (own.OwnerKind, own.OwnerId) : (FleetPrincipalKinds.Person, impliedPerson);
            if (ownerId is null) return;
            var suspended = ownerKind == FleetPrincipalKinds.Person && snapshot.Person(ownerId) is { IsActive: false };
            resources[(kind, id)] = new ResourceOwnerDto(kind, id, name, ownerKind, ownerId, SubjectName(snapshot, ownerKind, ownerId), own?.AuthorPersonId ?? author,
                own?.AuthorSessionId, own?.Paused ?? false, suspended);
        }
    }

    // A new owner as the request names it: an active person, a living team, or this organization.
    private static (string Kind, string Id) ResolveOwner(AccessSnapshot snapshot, string organizationId, string? kind, string? id) => kind switch
    {
        FleetPrincipalKinds.Person when snapshot.Person(id) is { IsActive: true } person => (kind, person.Id),
        FleetPrincipalKinds.Team when snapshot.Team(id) is { } team => (kind, team.Id),
        FleetPrincipalKinds.Organization => (kind, organizationId),
        FleetPrincipalKinds.Person or FleetPrincipalKinds.Team => throw NotFound("The new owner is not known, or is suspended."),
        _ => throw Invalid("A resource goes to a person, a team or the organization."),
    };

    // Whether a person answers for an owner: the person themselves, a maintainer of the team, an owner or admin for the organization.
    private static bool Accepts(AccessSnapshot snapshot, PersonRecord person, string kind, string id) => kind switch
    {
        FleetPrincipalKinds.Person => id == person.Id,
        FleetPrincipalKinds.Team => snapshot.TeamRole(id, person.Id) == FleetTeamRoles.Maintainer,
        FleetPrincipalKinds.Organization => FleetOrganizationRoles.Administers(person.Role),
        _ => false,
    };

    private static bool Concerns(AccessSnapshot snapshot, string personId, string kind, string id)
        => kind == FleetPrincipalKinds.Person ? id == personId : kind == FleetPrincipalKinds.Team && snapshot.TeamRole(id, personId) is not null;
}
