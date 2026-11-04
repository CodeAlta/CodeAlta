using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

// A person who leaves (doc/fleet/access-control.md, ownership over time).
internal sealed partial class AccessService
{
    /// <summary>Gets what refuses the pending devices and closes the connections of a suspended person; null for none (tests of the rules).</summary>
    public Func<DeviceRecord, RequestOrigin, CancellationToken, Task>? EndDevice { get; set; }

    /// <summary>
    /// Deprovisions a person who leaves: suspended at once (the server refuses their devices), their teams and grants taken
    /// back, what they owned given to a successor (a person or a team; the organization when none is named), paused until
    /// that owner resumes it, their pending devices refused and their connections closed. Their approved devices leave the
    /// device list by entries the console signs, and the next signed access document lists them as suspended, which stops
    /// their work on team machines. Only an owner deprovisions an owner or an admin; nobody deprovisions themselves.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="personId">The person.</param>
    /// <param name="request">The successor.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What was done, and the devices the console removes from the device list.</returns>
    /// <exception cref="CoordinatorException">The caller may not, the person or the successor is unknown, or the person is the last owner.</exception>
    public async Task<DeprovisionResultDto> DeprovisionAsync(DeviceRecord actor, string personId, DeprovisionRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.manage", AccessTarget.Organization);
        var leaving = snapshot.Person(personId) ?? throw NotFound("No person of the organization has this id.");
        if (leaving.Id == person.Id) throw Invalid("A person does not deprovision themselves: another owner or admin does.");
        if (!leaving.IsActive) throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "This person is suspended already.");
        if (FleetOrganizationRoles.Administers(leaving.Role) && person.Role != FleetOrganizationRoles.Owner) throw Denied("Only an owner deprovisions an owner or an admin.");
        var (successorKind, successorId) = request?.SuccessorKind switch
        {
            null => (FleetPrincipalKinds.Organization, actor.OrganizationId),
            FleetPrincipalKinds.Person when request.SuccessorId != personId && snapshot.Person(request.SuccessorId) is { IsActive: true } successor
                => (FleetPrincipalKinds.Person, successor.Id),
            FleetPrincipalKinds.Team when snapshot.Team(request.SuccessorId) is { } team => (FleetPrincipalKinds.Team, team.Id),
            FleetPrincipalKinds.Organization => (FleetPrincipalKinds.Organization, actor.OrganizationId),
            _ => throw Invalid("The successor is an active person other than the one who leaves, a team, or the organization."),
        };

        var now = _time.GetUtcNow();
        var devices = (await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false)).Where(device => device.OwnerPersonId == personId).ToList();
        var personal = new HashSet<string>(devices.Where(static device => device.Kind == FleetDeviceKinds.Node).Select(static device => device.Id), StringComparer.Ordinal);
        var transfers = new List<TransferRecord>();
        var owners = new List<ResourceOwnerRecord>();
        foreach (var resource in await ListResourcesAsync(actor.OrganizationId, snapshot, cancellationToken).ConfigureAwait(false))
        {
            if (resource.OwnerKind != FleetPrincipalKinds.Person || resource.OwnerId != personId) continue;
            // Their personal machines leave the fleet with their devices; what they owned beyond goes on, paused, with its new owner.
            if (resource.ResourceKind == FleetResourceKinds.Machine && personal.Contains(resource.ResourceId)) continue;
            transfers.Add(new TransferRecord(SetupService.NewId(), actor.OrganizationId, resource.ResourceKind, resource.ResourceId, FleetPrincipalKinds.Person, personId,
                successorKind, successorId, FleetTransferStates.Accepted, FleetTransferReasons.Deprovision, person.Id, now, person.Id, now));
            var held = snapshot.Owner(resource.ResourceKind, resource.ResourceId);
            owners.Add(new ResourceOwnerRecord(actor.OrganizationId, resource.ResourceKind, resource.ResourceId, successorKind, successorId,
                held?.AuthorPersonId ?? resource.AuthorId, held?.AuthorSessionId, Paused: true, now));
        }

        var audit = new List<AuditEntry>
        {
            origin.Audit("person.deprovision", actor, personId, details: Json(("successor", successorKind + ":" + successorId), ("role", leaving.Role))),
        };
        audit.AddRange(transfers.Select(transfer => origin.Audit("transfer.accept", actor, transfer.ResourceId, details: TransferDetails(transfer))));
        var outcome = await _access.SuspendPersonAsync(actor.OrganizationId, personId, now, transfers, owners, audit, cancellationToken).ConfigureAwait(false);
        if (outcome == RoleChangeOutcome.NotFound) throw NotFound("No person of the organization has this id.");
        if (outcome == RoleChangeOutcome.LastOwner) throw LastOwner();

        foreach (var device in devices.Where(static device => device.Status != DeviceStatuses.Revoked))
        {
            if (EndDevice is { } end) await end(device, origin, cancellationToken).ConfigureAwait(false);
        }

        Changed(actor.OrganizationId);
        var after = await _access.ReadAccessAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var approved = devices.Where(static device => device.Status == DeviceStatuses.Approved).Select(static device => device.Id).Order(StringComparer.Ordinal).ToList();
        return new DeprovisionResultDto(ToDto(after, after.Person(personId)!, devices), [.. transfers.Select(transfer => ToDto(transfer, null))], approved,
            [.. approved.Where(personal.Contains)]);
    }
}
