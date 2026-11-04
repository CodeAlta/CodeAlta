using System.Collections.Immutable;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The signed access document at the server (<c>doc/fleet/access-control.md</c>, what the node decides): the server keeps its
/// chain and offers a draft of the next version from its model (owners, admins, teams, team machines, suspended people); a
/// signer's console checks the draft, says what changes, and signs it. The server checks a version as every device does
/// (<see cref="FleetAccessDocuments.Verify"/>) and that it says what its model says; it never writes one.
/// </summary>
internal sealed class AccessDocumentService
{
    private readonly ICoordinatorStore _store;
    private readonly IAccessStore _access;
    private readonly AccessService _service;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="AccessDocumentService"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="access">The store of access control.</param>
    /// <param name="service">Access control, whose model the document says.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AccessDocumentService(ICoordinatorStore store, IAccessStore access, AccessService service, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _access = access;
        _service = service;
        _time = time;
        service.DocumentState = StateAsync;
        service.SignsNext = SignsNextAsync;
    }

    /// <summary>Lists the versions after a number, for any approved device of the organization: nodes verify them themselves.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="after">The number after which to list.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The versions.</returns>
    /// <exception cref="CoordinatorException">The caller is not an approved device.</exception>
    public async Task<AccessDocumentListDto> ListAsync(DeviceRecord actor, long after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Status != DeviceStatuses.Approved) throw AccessService.Denied("Only a device of the organization reads its access document.");
        var documents = await _access.ListAccessDocumentsAsync(actor.OrganizationId, after, cancellationToken).ConfigureAwait(false);
        return new AccessDocumentListDto([.. documents.Select(ToDto)]);
    }

    /// <summary>Gives the next version from the server's model, unsigned, with the names of what it names; for signers.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The draft.</returns>
    /// <exception cref="CoordinatorException">The caller is not a signer.</exception>
    public async Task<AccessDocumentDraftDto> DraftAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await _service.ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        if (!AccessPolicy.IsSigner(snapshot, person.Id)) throw AccessService.Denied("Only the devices of owners and admins sign the access document.");
        if (!await SignsNextAsync(actor.OrganizationId, snapshot, person.Id, cancellationToken).ConfigureAwait(false))
            throw AccessService.Denied("An owner signs first: the last signed version does not list this person as an owner or an admin who signs.");
        var documents = await _access.ListAccessDocumentsAsync(actor.OrganizationId, 0, cancellationToken).ConfigureAwait(false);
        var last = await _store.GetLastDeviceListEntryAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var draft = await ModelAsync(actor.OrganizationId, snapshot, documents.Count == 0 ? null : documents[^1], last?.Sequence ?? 1, cancellationToken).ConfigureAwait(false);
        var changes = documents.Count == 0 || !FleetAccessDocuments.TryParse(documents[^1].Payload, out var held) || !FleetAccessDocuments.SaySame(held, draft);
        var devices = await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var other in snapshot.People) names[other.Id] = other.DisplayName;
        foreach (var team in snapshot.Teams) names[team.Id] = team.Name;
        foreach (var identity in snapshot.ServiceIdentities) names[identity.Id] = identity.Name;
        foreach (var device in devices) names[device.Id] = device.Name;
        return new AccessDocumentDraftDto(draft, changes, names);
    }

    /// <summary>
    /// Appends a version a signer's controller device signed: it follows the last one, verifies as devices verify it against
    /// the device list the server holds, and says what the server's model says now.
    /// </summary>
    /// <param name="actor">The calling controller device, which signed it.</param>
    /// <param name="request">The signed version.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The version as stored.</returns>
    /// <exception cref="CoordinatorException">The version does not hold, does not follow the last one, or says something else than the model.</exception>
    public async Task<AccessDocumentDto> AppendAsync(DeviceRecord actor, AppendAccessDocumentRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await _service.ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        if (!AccessPolicy.IsSigner(snapshot, person.Id) || !await SignsNextAsync(actor.OrganizationId, snapshot, person.Id, cancellationToken).ConfigureAwait(false))
            throw AccessService.Denied("Only the devices of the owners and admins the last signed version lists sign the next one.");
        if (!FleetDevicePublicKey.TryParse(actor.PublicKey, out var key)) throw new InvalidOperationException("The key of a device in the table does not read.");
        if (!FleetAccessDocuments.TryReadSigned(request?.Document, key, out var document, out var reason)) throw Invalid(reason);
        if (document.OrganizationId != actor.OrganizationId) throw Invalid("it names another organization");
        var entries = await _store.ListDeviceListAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var devices = FleetDeviceListVerifier.VerifyOrThrow(entries.Select(static entry => new FleetSignedDeviceListEntry(entry.Payload, entry.Signature)), actor.OrganizationId,
            FleetDeviceListMode.Tolerant);
        var held = await _access.ListAccessDocumentsAsync(actor.OrganizationId, 0, cancellationToken).ConfigureAwait(false);
        var chain = FleetAccessDocuments.Verify([.. held.Select(static item => new FleetSignedAccessDocument(item.Payload, item.Signature)), request!.Document], devices,
            actor.OrganizationId);
        if (!chain.IsValid) throw Invalid(chain.Reason!);
        var model = await ModelAsync(actor.OrganizationId, snapshot, held.Count == 0 ? null : held[^1], document.DeviceListSequence, cancellationToken).ConfigureAwait(false);
        if (!FleetAccessDocuments.SaySame(model, document)) throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "Access changed meanwhile: read the draft again and sign it.");
        var record = new AccessDocumentRecord(actor.OrganizationId, document.Number, request.Document.Payload, request.Document.Signature, actor.Id, chain.State.LastHash!,
            _time.GetUtcNow());
        if (!await _access.AppendAccessDocumentAsync(record, origin.Audit("access_document.sign", actor, document.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                details: AccessService.Json(("signer", person.Id), ("teams", document.Teams.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("suspended", document.Suspended.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))), cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "Another version was signed meanwhile: read the draft again and sign it.");
        _service.Changed(actor.OrganizationId);
        return ToDto(record);
    }

    /// <summary>
    /// Whether a person's devices sign the next version, as the chain says it: an owner or admin of the last version who is
    /// not suspended there, or, before the first version, the person who set the server up.
    /// </summary>
    private async Task<bool> SignsNextAsync(string organizationId, AccessSnapshot snapshot, string personId, CancellationToken cancellationToken)
    {
        if (snapshot.Person(personId) is not { IsActive: true }) return false;
        var documents = await _access.ListAccessDocumentsAsync(organizationId, 0, cancellationToken).ConfigureAwait(false);
        if (documents.Count > 0)
            return FleetAccessDocuments.TryParse(documents[^1].Payload, out var last) && !last.Suspended.Contains(personId, StringComparer.Ordinal)
                && (last.Owners.Contains(personId, StringComparer.Ordinal) || last.Signers.Contains(personId, StringComparer.Ordinal));
        var entries = await _store.ListDeviceListAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var list = FleetDeviceListVerifier.Verify(entries.Select(static entry => new FleetSignedDeviceListEntry(entry.Payload, entry.Signature)), organizationId,
            FleetDeviceListMode.Tolerant).State;
        return list.FirstDeviceId is { } first && list.Tenures.TryGetValue(first, out var tenure) && tenure.Member.OwnerId == personId;
    }

    /// <summary>Where the document stands for People and access: its last version, and what the model says that machines do not see yet.</summary>
    private async Task<AccessDocumentStateDto> StateAsync(string organizationId, AccessSnapshot snapshot, CancellationToken cancellationToken)
    {
        var documents = await _access.ListAccessDocumentsAsync(organizationId, 0, cancellationToken).ConfigureAwait(false);
        var last = documents.Count == 0 ? null : documents[^1];
        var model = await ModelAsync(organizationId, snapshot, last, 1, cancellationToken).ConfigureAwait(false);
        var before = FleetAccessState.Empty;
        FleetAccessDocument? held = null;
        if (last is not null && FleetAccessDocuments.TryParse(last.Payload, out held))
        {
            before = new FleetAccessState(organizationId, held.Number, last.Hash, held.DeviceListSequence, [.. held.Owners], [.. held.Signers],
                held.Teams.ToDictionary(static team => team.Id, StringComparer.Ordinal).ToImmutableDictionary(StringComparer.Ordinal),
                held.Machines.ToDictionary(static machine => machine.DeviceId, static machine => machine.TeamId, StringComparer.Ordinal).ToImmutableDictionary(StringComparer.Ordinal),
                [.. held.Suspended])
            {
                OrganizationMachines = [.. held.OrganizationMachines ?? []], OrganizationServices = [.. held.OrganizationServices ?? []],
                Automations = (held.Automations ?? []).ToImmutableDictionary(static automation => automation.Id, StringComparer.Ordinal),
                Approvers = FleetAccessState.ApproversOf(held.Approvers),
                ApproversUntil = FleetAccessState.UntilOf(held.Approvers),
            };
        }

        // A home server with no team and nobody suspended has nothing for machines to verify: its first version waits for a need.
        var needed = last is not null || model.Teams.Count > 0 || model.Suspended.Count > 0 || model.OrganizationMachines is not null || model.Automations is not null
            || model.Approvers is { Count: > 0 };
        var names = (await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(static device => device.Id, static device => device.Name,
            StringComparer.Ordinal);
        foreach (var person in snapshot.People) names[person.Id] = person.DisplayName;
        foreach (var team in snapshot.Teams) names[team.Id] = team.Name;
        foreach (var identity in snapshot.ServiceIdentities) names[identity.Id] = identity.Name;
        IReadOnlyList<string> pending = !needed ? [] : FleetAccessDocuments.Describe(before, model, id => names.GetValueOrDefault(id));
        if (needed && last is null && pending.Count == 0) pending = ["The first version of the access document"];
        var signer = last is null ? null : (await _store.GetDeviceAsync(last.SignerDeviceId, cancellationToken).ConfigureAwait(false))?.OwnerPersonId;
        return new AccessDocumentStateDto(last?.Number ?? 0, signer is null ? null : snapshot.Person(signer)?.DisplayName, last?.RecordedAt, pending);
    }

    /// <summary>The next version as the server's model says it: active owners, active admins, living teams of active people, team machines, suspended people.</summary>
    private async Task<FleetAccessDocument> ModelAsync(string organizationId, AccessSnapshot snapshot, AccessDocumentRecord? last, long listSequence,
        CancellationToken cancellationToken)
    {
        var devices = await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var approved = new HashSet<string>(devices.Where(static device => device.Status == DeviceStatuses.Approved).Select(static device => device.Id), StringComparer.Ordinal);
        var active = snapshot.People.Where(static person => person.IsActive).ToList();
        var teams = snapshot.Teams.Where(static team => team.DissolvedAt is null).OrderBy(static team => team.Id, StringComparer.Ordinal)
            .Select(team =>
            {
                var members = snapshot.Members.Where(member => member.TeamId == team.Id && snapshot.Person(member.PersonId) is { IsActive: true }).ToList();
                // The service identities of the team that are on: its automations run as one of them, and machines follow them once signed.
                var services = snapshot.ServiceIdentities.Where(identity => identity.Enabled && identity.OwnerKind == FleetPrincipalKinds.Team && identity.OwnerId == team.Id)
                    .Select(static identity => identity.Id).Order(StringComparer.Ordinal).ToArray();
                return new FleetAccessTeam(team.Id,
                    [.. members.Where(static member => member.Role == FleetTeamRoles.Maintainer).Select(static member => member.PersonId).Order(StringComparer.Ordinal)],
                    [.. members.Where(static member => member.Role != FleetTeamRoles.Maintainer).Select(static member => member.PersonId).Order(StringComparer.Ordinal)],
                    team.RunsMemberAutomations, services.Length == 0 ? null : services);
            }).ToList();
        var living = new HashSet<string>(teams.Select(static team => team.Id), StringComparer.Ordinal);
        // The machines of the organization and the service identities it turned on: its automations run there, as one of them.
        var organizationMachines = snapshot.Owners.Where(owner => owner.ResourceKind == FleetResourceKinds.Machine && owner.OwnerKind == FleetPrincipalKinds.Organization
                && approved.Contains(owner.ResourceId)).Select(static owner => owner.ResourceId).Order(StringComparer.Ordinal).ToArray();
        // The automations whose owner machines follow: given to another owner, or of a team or of the organization; a person who
        // gives versions to their own machines needs none.
        var automations = snapshot.Owners.Where(static owner => owner.ResourceKind == FleetResourceKinds.Automation).OrderBy(static owner => owner.ResourceId, StringComparer.Ordinal)
            .Select(static owner => new FleetAccessAutomation(owner.ResourceId, owner.OwnerKind, owner.OwnerId, owner.Paused)).ToArray();
        var organizationServices = snapshot.ServiceIdentities.Where(static identity => identity.Enabled && identity.OwnerKind == FleetPrincipalKinds.Organization)
            .Select(static identity => identity.Id).Order(StringComparer.Ordinal).ToArray();
        var machines = snapshot.Owners.Where(owner => owner.ResourceKind == FleetResourceKinds.Machine && owner.OwnerKind == FleetPrincipalKinds.Team && living.Contains(owner.OwnerId)
                && approved.Contains(owner.ResourceId))
            .OrderBy(static owner => owner.ResourceId, StringComparer.Ordinal).Select(static owner => new FleetAccessMachine(owner.ResourceId, owner.OwnerId)).ToList();
        var approvers = await _service.DocumentApproversAsync(organizationId, snapshot, cancellationToken).ConfigureAwait(false);
        return new FleetAccessDocument(FleetAccessDocument.CurrentVersion, organizationId, (last?.Number ?? 0) + 1, last?.Hash, listSequence,
            [.. active.Where(static person => person.Role == FleetOrganizationRoles.Owner).Select(static person => person.Id).Order(StringComparer.Ordinal)],
            [.. active.Where(static person => person.Role == FleetOrganizationRoles.Admin).Select(static person => person.Id).Order(StringComparer.Ordinal)],
            teams, machines,
            [.. snapshot.People.Where(static person => !person.IsActive).Select(static person => person.Id).Order(StringComparer.Ordinal)],
            string.Empty, default, organizationMachines.Length == 0 ? null : organizationMachines, organizationServices.Length == 0 ? null : organizationServices,
            automations.Length == 0 ? null : automations)
        {
            // Who else takes the runs of an automation over, for machines to check; a version without any leaves the field out.
            Approvers = approvers.Count > 0 ? approvers : null,
        };
    }

    private static AccessDocumentDto ToDto(AccessDocumentRecord record)
        => new(record.Number, record.Hash, new FleetSignedAccessDocument(record.Payload, record.Signature), record.RecordedAt);

    private static CoordinatorException Invalid(string reason)
        => CoordinatorException.BadRequest(CoordinatorErrorCodes.AccessDocumentInvalid, $"The version of the access document does not hold: {reason}.");
}
