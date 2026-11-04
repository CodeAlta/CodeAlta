using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// Access control of the organization on the REST API (<c>doc/fleet/access-control.md</c>): people and their roles, invitations,
/// teams and their roles, service identities, grants, the owners of resources and their transfers, who can do what, and the
/// proposals of agents. Every decision is taken by <see cref="AccessPolicy"/> on a snapshot read at the call; every change is
/// audited with it and told to the devices of the organization (<see cref="FleetAccessMethods.Changed"/>).
/// </summary>
/// <remarks>
/// The server decides what it answers and records; machines decide what they run from the signed access document
/// (<see cref="AccessDocumentService"/>), which the server cannot write.
/// </remarks>
internal sealed partial class AccessService
{
    /// <summary>The most transfers and proposals listed.</summary>
    public const int MaxListed = 200;

    /// <summary>The shortest life of an invitation.</summary>
    public static readonly TimeSpan MinInvitationLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest life of an invitation.</summary>
    public static readonly TimeSpan MaxInvitationLifetime = TimeSpan.FromDays(7);

    /// <summary>The life of an invitation when none is asked.</summary>
    public static readonly TimeSpan DefaultInvitationLifetime = TimeSpan.FromDays(3);

    private const string InvitationPrefix = "aet_";
    private const int MaxSummaryLength = 500;
    private const int MaxPayloadLength = 8192;
    private const int MaxSealedLength = 65536;
    private const int MaxWraps = 200;
    private const int MaxWrappedLength = 8192;

    private readonly ICoordinatorStore _store;
    private readonly IAccessStore _access;
    private readonly IAutomationStore _automations;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="AccessService"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="access">The store of access control.</param>
    /// <param name="automations">The store of automations, whose owners this service reads and transfers.</param>
    /// <param name="notifications">Tells devices that access changed.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AccessService(ICoordinatorStore store, IAccessStore access, IAutomationStore automations, NotificationService notifications, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(automations);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _access = access;
        _automations = automations;
        _notifications = notifications;
        _time = time;
    }

    /// <summary>Gets where the signed access document stands, for a person; set once the document service exists.</summary>
    public Func<string, AccessSnapshot, CancellationToken, Task<AccessDocumentStateDto>>? DocumentState { get; set; }

    /// <summary>
    /// Gets whether a person's devices sign the next version of the access document, as the chain says it (the owners and
    /// admins of the last version, the founder for the first); set once the document service exists.
    /// </summary>
    public Func<string, AccessSnapshot, string, CancellationToken, Task<bool>>? SignsNext { get; set; }

    /// <summary>Reads what the calling device's person is and may do.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The person, their capabilities at the organization's scope and where the signed document stands.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<AccessSelfDto> SelfAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var devices = await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var document = DocumentState is { } state ? await state(actor.OrganizationId, snapshot, cancellationToken).ConfigureAwait(false) : new AccessDocumentStateDto(0, null, null, []);
        var signer = AccessPolicy.IsSigner(snapshot, person.Id)
            && (SignsNext is not { } signs || await signs(actor.OrganizationId, snapshot, person.Id, cancellationToken).ConfigureAwait(false));
        return new AccessSelfDto(ToDto(snapshot, person, devices), AccessPolicy.OrganizationCapabilities(snapshot, person.Id, now), IsAdvanced(snapshot), document, signer);
    }

    /// <summary>Lists the people of the organization with their roles, status and teams.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The people.</returns>
    /// <exception cref="CoordinatorException">The caller may not see the people of the organization.</exception>
    public async Task<AccessListDto<PersonAccessDto>> ListPeopleAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.view", AccessTarget.Organization);
        var devices = await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        return new([.. snapshot.People.Select(other => ToDto(snapshot, other, devices))]);
    }

    /// <summary>
    /// Invites a person: they are added with a role, and get a code their computer enrolls with as their first controller
    /// device, which the owner then approves as for a person company sign-in created. Only an owner invites an owner or an admin.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="request">Who, which role, how long the code works.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The person and their code, shown once.</returns>
    /// <exception cref="CoordinatorException">The caller may not invite, or the request does not hold.</exception>
    public async Task<InvitationDto> InviteAsync(DeviceRecord actor, InvitePersonRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.manage", AccessTarget.Organization);
        var name = SetupService.RequireName(request?.DisplayName, "displayName");
        var role = request?.Role ?? FleetOrganizationRoles.Member;
        if (!FleetOrganizationRoles.IsKnown(role)) throw Invalid($"role is one of {string.Join(", ", FleetOrganizationRoles.All)}.");
        if (FleetOrganizationRoles.Administers(role) && person.Role != FleetOrganizationRoles.Owner) throw Denied("Only an owner makes owners and admins.");
        var lifetime = request?.TtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultInvitationLifetime;
        if (lifetime < MinInvitationLifetime || lifetime > MaxInvitationLifetime)
            throw Invalid($"ttlSeconds is from {MinInvitationLifetime.TotalSeconds:0} to {MaxInvitationLifetime.TotalSeconds:0}.");
        var now = _time.GetUtcNow();
        var invited = new PersonRecord(SetupService.NewId(), actor.OrganizationId, name, role, now);
        Span<byte> secret = stackalloc byte[32];
        RandomNumberGenerator.Fill(secret);
        var token = InvitationPrefix + Base64Url.EncodeToString(secret);
        var invitation = new EnrollmentTokenRecord(SetupService.NewId(), actor.OrganizationId, DeviceService.HashToken(token), "invitation", invited.Id, actor.Id, now,
            now + lifetime, null, null);
        await _access.AddPersonAsync(invited, invitation, origin.Audit("person.invite", actor, invited.Id, details: Json(("role", role))), cancellationToken).ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return new InvitationDto(ToDto(snapshot with { People = [.. snapshot.People, invited] }, invited, []), token, invitation.ExpiresAt);
    }

    /// <summary>Changes a person's role; only an owner gives or takes the roles of owner and admin, and the last owner keeps theirs.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="personId">The person.</param>
    /// <param name="request">The new role.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The person as now.</returns>
    /// <exception cref="CoordinatorException">The caller may not, the person is unknown, or they are the last owner.</exception>
    public async Task<PersonAccessDto> SetRoleAsync(DeviceRecord actor, string personId, SetRoleRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.manage", AccessTarget.Organization);
        var role = request?.Role;
        if (!FleetOrganizationRoles.IsKnown(role)) throw Invalid($"role is one of {string.Join(", ", FleetOrganizationRoles.All)}.");
        var target = snapshot.Person(personId) ?? throw NotFound("No person of the organization has this id.");
        if ((FleetOrganizationRoles.Administers(role) || FleetOrganizationRoles.Administers(target.Role)) && person.Role != FleetOrganizationRoles.Owner)
            throw Denied("Only an owner gives or takes the roles of owner and admin.");
        var outcome = await _access.SetPersonRoleAsync(actor.OrganizationId, personId, role!,
            origin.Audit("person.role", actor, personId, details: Json(("from", target.Role), ("to", role!))), cancellationToken).ConfigureAwait(false);
        if (outcome == RoleChangeOutcome.NotFound) throw NotFound("No person of the organization has this id.");
        if (outcome == RoleChangeOutcome.LastOwner) throw LastOwner();
        Changed(actor.OrganizationId);
        return await PersonAsync(actor.OrganizationId, personId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the teams with their members and machines.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The teams that live.</returns>
    /// <exception cref="CoordinatorException">The caller may not see the people of the organization.</exception>
    public async Task<AccessListDto<TeamDto>> ListTeamsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.view", AccessTarget.Organization);
        return new(await WithRotationsAsync(actor.OrganizationId, snapshot, [.. snapshot.Teams.Where(static team => team.DissolvedAt is null).Select(team => ToDto(snapshot, team))],
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Makes a team; the person who makes it is its first maintainer.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="request">Its name.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The team.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the name does not hold or is taken.</exception>
    public async Task<TeamDto> CreateTeamAsync(DeviceRecord actor, CreateTeamRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.manage", AccessTarget.Organization);
        var name = SetupService.RequireName(request?.Name, "name");
        if (snapshot.Teams.Any(team => team.DissolvedAt is null && string.Equals(team.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "Another team has this name.");
        var now = _time.GetUtcNow();
        var team = new TeamRecord(SetupService.NewId(), actor.OrganizationId, name, false, now, person.Id, null, 1);
        if (!await _access.PutTeamAsync(team, origin.Audit("team.create", actor, team.Id, details: Json(("name", name))), cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The team exists already.");
        await _access.SetTeamMemberAsync(team.Id, person.Id, FleetTeamRoles.Maintainer, now,
            origin.Audit("team.member", actor, team.Id, details: Json(("person", person.Id), ("role", FleetTeamRoles.Maintainer))), cancellationToken).ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return await TeamAsync(actor.OrganizationId, team.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames a team or changes whether its machines run the automations of its members; for its maintainers and admins.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="request">What changes.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The team.</returns>
    /// <exception cref="CoordinatorException">The caller may not, the team is unknown, or the change does not hold.</exception>
    public async Task<TeamDto> UpdateTeamAsync(DeviceRecord actor, string teamId, UpdateTeamRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var team = snapshot.Team(teamId) ?? throw NotFound("No team has this id.");
        Require(snapshot, person, "access.manage", AccessTarget.Team(teamId));
        var name = request?.Name is null ? team.Name : SetupService.RequireName(request.Name, "name");
        if (snapshot.Teams.Any(other => other.Id != teamId && other.DissolvedAt is null && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "Another team has this name.");
        var changed = team with { Name = name, RunsMemberAutomations = request?.RunsMemberAutomations ?? team.RunsMemberAutomations, Version = team.Version + 1 };
        if (!await _access.PutTeamAsync(changed, origin.Audit("team.update", actor, teamId, details: Json(("name", changed.Name),
                ("runsMemberAutomations", changed.RunsMemberAutomations ? "true" : "false"))), cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The team changed meanwhile: read it again.");
        Changed(actor.OrganizationId);
        return await TeamAsync(actor.OrganizationId, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dissolves a team: what it owned goes to the organization (owners and admins decide what next), never vanishes, and its
    /// members leave it. For admins.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The transfers made to the organization.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the team is unknown.</exception>
    public async Task<AccessListDto<TransferDto>> DissolveTeamAsync(DeviceRecord actor, string teamId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var team = snapshot.Team(teamId) ?? throw NotFound("No team has this id.");
        Require(snapshot, person, "access.manage", AccessTarget.Organization);
        var now = _time.GetUtcNow();
        var transfers = new List<TransferDto>();
        foreach (var owned in snapshot.Owners.Where(owner => owner.OwnerKind == FleetPrincipalKinds.Team && owner.OwnerId == teamId))
        {
            var transfer = new TransferRecord(SetupService.NewId(), actor.OrganizationId, owned.ResourceKind, owned.ResourceId, FleetPrincipalKinds.Team, teamId,
                FleetPrincipalKinds.Organization, actor.OrganizationId, FleetTransferStates.Offered, FleetTransferReasons.Dissolve, person.Id, now, null, null);
            await _access.OfferTransferAsync(transfer, origin.Audit("transfer.offer", actor, owned.ResourceId, details: TransferDetails(transfer)), cancellationToken)
                .ConfigureAwait(false);
            var accepted = await _access.DecideTransferAsync(transfer.Id, FleetTransferStates.Accepted, person.Id, now,
                owned with { OwnerKind = FleetPrincipalKinds.Organization, OwnerId = actor.OrganizationId, Paused = owned.ResourceKind == FleetResourceKinds.Automation, UpdatedAt = now },
                origin.Audit("transfer.accept", actor, owned.ResourceId, details: TransferDetails(transfer)), cancellationToken).ConfigureAwait(false);
            if (accepted is not null) transfers.Add(ToDto(accepted, null));
        }

        if (!await _access.PutTeamAsync(team with { DissolvedAt = now, Version = team.Version + 1 }, origin.Audit("team.dissolve", actor, teamId), cancellationToken)
                .ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The team changed meanwhile: read it again.");
        Changed(actor.OrganizationId);
        return new(transfers);
    }

    /// <summary>Sets a person's role in a team, or takes them out (null); for its maintainers and admins. A team keeps a maintainer while it has members.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="personId">The person.</param>
    /// <param name="request">Their role, or null to take them out.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The team.</returns>
    /// <exception cref="CoordinatorException">The caller may not, the team or the person is unknown, or the team would keep members without a maintainer.</exception>
    public async Task<TeamDto> SetTeamMemberAsync(DeviceRecord actor, string teamId, string personId, SetTeamMemberRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        _ = snapshot.Team(teamId) ?? throw NotFound("No team has this id.");
        Require(snapshot, person, "access.manage", AccessTarget.Team(teamId));
        var role = request?.Role;
        if (role is not null && !FleetTeamRoles.IsKnown(role)) throw Invalid("role is maintainer or member.");
        var member = snapshot.Person(personId) ?? throw NotFound("No person of the organization has this id.");
        if (role is not null && !member.IsActive) throw Invalid("A suspended person joins no team.");
        var members = snapshot.Members.Where(other => other.TeamId == teamId && other.PersonId != personId).ToList();
        if (role != FleetTeamRoles.Maintainer && members.Count > 0 && members.All(static other => other.Role != FleetTeamRoles.Maintainer))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "A team with members keeps a maintainer: make another one first.");
        await _access.SetTeamMemberAsync(teamId, personId, role, _time.GetUtcNow(),
            origin.Audit(role is null ? "team.member.remove" : "team.member", actor, teamId, details: Json(("person", personId), ("role", role ?? "none"))), cancellationToken)
            .ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return await TeamAsync(actor.OrganizationId, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the service identities.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identities.</returns>
    /// <exception cref="CoordinatorException">The caller may not see the people of the organization.</exception>
    public async Task<AccessListDto<ServiceIdentityDto>> ListServiceIdentitiesAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.view", AccessTarget.Organization);
        return new([.. snapshot.ServiceIdentities.Select(ToDto)]);
    }

    /// <summary>Makes a service identity of a team or the organization, disabled until a person enables it.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="request">Its name and owner.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the request does not hold.</exception>
    public async Task<ServiceIdentityDto> CreateServiceIdentityAsync(DeviceRecord actor, CreateServiceIdentityRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var name = SetupService.RequireName(request?.Name, "name");
        var (ownerKind, ownerId) = (request?.OwnerKind, request?.OwnerId);
        if (ownerKind == FleetPrincipalKinds.Team)
        {
            _ = snapshot.Team(ownerId) ?? throw NotFound("No team has this id.");
            Require(snapshot, person, "access.manage", AccessTarget.Team(ownerId!));
        }
        else if (ownerKind == FleetPrincipalKinds.Organization)
        {
            Require(snapshot, person, "access.manage", AccessTarget.Organization);
            ownerId = actor.OrganizationId;
        }
        else
        {
            throw Invalid("A service identity belongs to a team or to the organization.");
        }

        var identity = new ServiceIdentityRecord(SetupService.NewId(), actor.OrganizationId, name, ownerKind, ownerId!, false, _time.GetUtcNow(), person.Id);
        await _access.PutServiceIdentityAsync(identity, origin.Audit("service_identity.create", actor, identity.Id, details: Json(("owner", ownerKind + ":" + ownerId))),
            cancellationToken).ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return ToDto(identity);
    }

    /// <summary>Enables or disables a service identity: unattended work never starts acting by itself.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="identityId">The identity.</param>
    /// <param name="enabled">Whether it may act.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the identity is unknown.</exception>
    public async Task<ServiceIdentityDto> EnableServiceIdentityAsync(DeviceRecord actor, string identityId, bool enabled, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var identity = snapshot.ServiceIdentities.FirstOrDefault(other => other.Id == identityId) ?? throw NotFound("No service identity has this id.");
        Require(snapshot, person, "access.manage", identity.OwnerKind == FleetPrincipalKinds.Team ? AccessTarget.Team(identity.OwnerId) : AccessTarget.Organization);
        var changed = identity with { Enabled = enabled };
        await _access.PutServiceIdentityAsync(changed, origin.Audit(enabled ? "service_identity.enable" : "service_identity.disable", actor, identityId), cancellationToken)
            .ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return ToDto(changed);
    }

    /// <summary>Lists the grants: all of them for those who manage access or review it, those that name the caller or their teams for others.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The grants that are not revoked.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<AccessListDto<GrantDto>> ListGrantsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var all = AccessPolicy.Allows(snapshot, person.Id, "access.manage", AccessTarget.Organization, now)
            || AccessPolicy.Allows(snapshot, person.Id, "access.review", AccessTarget.Organization, now);
        return new([.. snapshot.Grants.Where(grant => all || grant.SubjectKind == FleetPrincipalKinds.Person && grant.SubjectId == person.Id
            || grant.SubjectKind == FleetPrincipalKinds.Team && snapshot.TeamRole(grant.SubjectId, person.Id) is not null
            || grant.ScopeKind == FleetScopeKinds.Team && snapshot.TeamRole(grant.ScopeId!, person.Id) == FleetTeamRoles.Maintainer).Select(ToDto)]);
    }

    /// <summary>
    /// Makes a grant. Granting needs to manage access at the grant's scope (a team's maintainers grant on the team, the owner
    /// of a resource on it). A content capability is granted on one resource only; a grant of sealed content waits for a
    /// device that holds the keys (<see cref="FleetGrantStatuses.PendingKeys"/>); a grant to a service identity needs it enabled.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="request">The grant.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the grant does not hold.</exception>
    public async Task<GrantDto> CreateGrantAsync(DeviceRecord actor, CreateGrantRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request is null) throw Invalid("The grant needs a subject, a capability and a scope.");
        if (!FleetPrincipalKinds.CanBeGranted(request.SubjectKind)) throw Invalid("A grant is for a person, a team or a service identity.");
        if (!FleetCapabilities.IsKnown(request.Capability)) throw Invalid($"'{request.Capability}' is not a capability.");
        if (!FleetScopeKinds.IsKnown(request.ScopeKind)) throw Invalid("The scope is organization, team, project or resource.");
        var subjectKnown = request.SubjectKind switch
        {
            FleetPrincipalKinds.Person => snapshot.Person(request.SubjectId) is { IsActive: true },
            FleetPrincipalKinds.Team => snapshot.Team(request.SubjectId) is not null,
            _ => snapshot.ServiceIdentities.Any(identity => identity.Id == request.SubjectId),
        };
        if (!subjectKnown) throw NotFound("The subject of the grant is not known, or is suspended.");
        var content = FleetCapabilities.IsContent(request.Capability);
        if (content && request.ScopeKind != FleetScopeKinds.Resource) throw Invalid("Content is granted on one resource, never on a scope: reading content does not flow down.");
        if (request.Sealed && (request.Capability != "session.view-content" || request.ScopeId?.StartsWith("session:", StringComparison.Ordinal) != true))
            throw Invalid("Only a grant of the content of a session is sealed: its keys go to the grantee.");
        if (request.ExpiresAt is { } expires && expires <= _time.GetUtcNow()) throw Invalid("expiresAt is in the past.");
        var target = await GrantTargetAsync(actor.OrganizationId, snapshot, request.ScopeKind, request.ScopeId, cancellationToken).ConfigureAwait(false);
        var manager = request.ScopeKind == FleetScopeKinds.Resource && target.ResourceKind is { } kind ? ManageCapability(kind) : "access.manage";
        Require(snapshot, person, manager, target);
        if (request.ScopeKind == FleetScopeKinds.Organization && !FleetOrganizationRoles.Administers(person.Role)) throw Denied("Only owners and admins grant on the whole organization.");
        var grant = new GrantRecord(SetupService.NewId(), actor.OrganizationId, request.SubjectKind, request.SubjectId, request.Capability, request.ScopeKind,
            request.ScopeKind == FleetScopeKinds.Organization ? null : request.ScopeId, request.Sealed ? FleetGrantStatuses.PendingKeys : FleetGrantStatuses.Active,
            _time.GetUtcNow(), person.Id, request.ExpiresAt, null);
        await _access.AddGrantAsync(grant, origin.Audit("grant.create", actor, grant.Id, details: GrantDetails(grant)), cancellationToken).ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return ToDto(grant);
    }

    /// <summary>Takes a grant back; for whoever could make it, and for its subject.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="grantId">The grant.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The grant, revoked.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the grant is unknown.</exception>
    public async Task<GrantDto> RevokeGrantAsync(DeviceRecord actor, string grantId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var grant = snapshot.Grants.FirstOrDefault(other => other.Id == grantId) ?? throw NotFound("No grant has this id.");
        var ownGrant = grant.SubjectKind == FleetPrincipalKinds.Person && grant.SubjectId == person.Id;
        if (!ownGrant)
        {
            var target = await GrantTargetAsync(actor.OrganizationId, snapshot, grant.ScopeKind, grant.ScopeId, cancellationToken).ConfigureAwait(false);
            Require(snapshot, person, grant.ScopeKind == FleetScopeKinds.Resource && target.ResourceKind is { } kind ? ManageCapability(kind) : "access.manage", target);
        }

        // Taking back a grant of sealed content starts a new key epoch for what it opened, on a device that holds the keys.
        var revoked = await _access.SetGrantStatusAsync(grantId, grant.Status, FleetGrantStatuses.Revoked, _time.GetUtcNow(),
            origin.Audit("grant.revoke", actor, grantId, details: GrantDetails(grant)), cancellationToken).ConfigureAwait(false)
            ?? throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The grant changed meanwhile: read it again.");
        Changed(actor.OrganizationId);
        return ToDto(revoked);
    }

    /// <summary>
    /// Confirms that the keys a grant of sealed content opens were wrapped to the devices of its subject, by a controller device
    /// of the owner of that content, which holds them (<c>doc/fleet/access-control.md</c>, access and keys). The server never
    /// does it by itself.
    /// </summary>
    /// <param name="actor">The calling controller device: one of the owner of the resource.</param>
    /// <param name="grantId">The grant.</param>
    /// <param name="request">For a grant of a session: its metadata sealed for the grant, and the copy of the key for each device of the grantee.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The grant, active.</returns>
    /// <exception cref="CoordinatorException">The caller is not a device of the owner, the keys do not hold, or the grant does not wait for its keys.</exception>
    public async Task<GrantDto> ConfirmGrantKeysAsync(DeviceRecord actor, string grantId, ConfirmGrantKeysRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var grant = snapshot.Grants.FirstOrDefault(other => other.Id == grantId) ?? throw NotFound("No grant has this id.");
        if (grant.Status != FleetGrantStatuses.PendingKeys) throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The grant does not wait for keys.");
        var target = await GrantTargetAsync(actor.OrganizationId, snapshot, grant.ScopeKind, grant.ScopeId, cancellationToken).ConfigureAwait(false);
        var holds = target.OwnerKind == FleetPrincipalKinds.Person ? target.OwnerId == person.Id
            : target.OwnerKind == FleetPrincipalKinds.Team && target.OwnerId is { } team && snapshot.TeamRole(team, person.Id) == FleetTeamRoles.Maintainer;
        if (!holds) throw Denied("Only a device of the owner of the content holds its keys and confirms them.");
        if (grant.Capability == "session.view-content")
        {
            // The keys of a session: its metadata sealed for the grant, and the copy of the key for each device of the grantee only.
            if (request is null || string.IsNullOrEmpty(request.Sealed) || request.Sealed.Length > MaxSealedLength || request.Wraps is not { Count: > 0 and <= MaxWraps })
                throw Invalid("The keys of a grant of a session are its sealed metadata and a copy of its key for each device of the grantee.");
            var recipients = await GranteeDevicesAsync(actor.OrganizationId, snapshot, grant, cancellationToken).ConfigureAwait(false);
            if (request.Wraps.Any(wrap => wrap is null || !recipients.Contains(wrap.DeviceId) || string.IsNullOrEmpty(wrap.Wrapped) || wrap.Wrapped.Length > MaxWrappedLength)
                || request.Wraps.Select(static wrap => wrap.DeviceId).Distinct(StringComparer.Ordinal).Count() != request.Wraps.Count)
                throw Invalid("A copy of the key goes once to each device of the grantee, and to no other.");
            var keyed = await _access.ActivateGrantKeysAsync(grantId, request.Sealed, [.. request.Wraps.Select(static wrap => (wrap.DeviceId, wrap.Wrapped))], _time.GetUtcNow(),
                origin.Audit("grant.keys", actor, grantId, details: GrantDetails(grant)), cancellationToken).ConfigureAwait(false)
                ?? throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The grant changed meanwhile: read it again.");
            Changed(actor.OrganizationId);
            return ToDto(keyed);
        }

        var active = await _access.SetGrantStatusAsync(grantId, FleetGrantStatuses.PendingKeys, FleetGrantStatuses.Active, _time.GetUtcNow(),
            origin.Audit("grant.keys", actor, grantId, details: GrantDetails(grant)), cancellationToken).ConfigureAwait(false)
            ?? throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The grant changed meanwhile: read it again.");
        Changed(actor.OrganizationId);
        return ToDto(active);
    }

    /// <summary>Lists what grants of sealed content give the calling device: the sealed metadata of each session, and its copy of the key.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The keys.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of an active person.</exception>
    public async Task<AccessListDto<GrantKeyDto>> ListGrantKeysAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        _ = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var keys = await _access.ListGrantKeysAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0) return new([]);
        var sessions = (await _store.ListSessionsAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(static session => session.Id, StringComparer.Ordinal);
        var listed = new List<GrantKeyDto>(keys.Count);
        foreach (var (grantId, scopeId, sealedText, wrapped) in keys)
        {
            if (scopeId is not { } scope || !scope.StartsWith("session:", StringComparison.Ordinal)) continue;
            var sessionId = scope["session:".Length..];
            if (sessions.TryGetValue(sessionId, out var session)) listed.Add(new GrantKeyDto(grantId, sessionId, session.MachineId, sealedText, wrapped));
        }

        return new(listed);
    }

    // The devices the keys of a grant go to: the approved controller devices of its person, or of the members of its team.
    private async Task<HashSet<string>> GranteeDevicesAsync(string organizationId, AccessSnapshot snapshot, GrantRecord grant, CancellationToken cancellationToken)
    {
        var people = grant.SubjectKind switch
        {
            FleetPrincipalKinds.Person => new HashSet<string>([grant.SubjectId], StringComparer.Ordinal),
            FleetPrincipalKinds.Team => snapshot.Members.Where(member => member.TeamId == grant.SubjectId).Select(static member => member.PersonId).ToHashSet(StringComparer.Ordinal),
            _ => [],
        };
        var devices = await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return devices.Where(device => device.Kind == FleetDeviceKinds.Controller && device.Status == DeviceStatuses.Approved && device.OwnerPersonId is { } owner
            && people.Contains(owner) && snapshot.Person(owner) is { IsActive: true }).Select(static device => device.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Lists who can do what, for an access review: the roles of people, the roles in teams, the grants, and the owners of
    /// resources; for owners, admins and auditors.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The lines of the review.</returns>
    /// <exception cref="CoordinatorException">The caller may not review access.</exception>
    public async Task<AccessReviewDto> ReviewAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, "access.review", AccessTarget.Organization);
        var now = _time.GetUtcNow();
        var rows = new List<AccessReviewRowDto>();
        foreach (var other in snapshot.People.Where(static other => other.IsActive))
        {
            foreach (var capability in AccessPolicy.OrganizationCapabilities(snapshot, other.Id, now))
                rows.Add(new(FleetPrincipalKinds.Person, other.Id, other.DisplayName, capability, FleetScopeKinds.Organization, null, "role:" + other.Role));
        }

        foreach (var member in snapshot.Members)
        {
            if (snapshot.Team(member.TeamId) is not { } team || snapshot.Person(member.PersonId) is not { IsActive: true } other) continue;
            rows.Add(new(FleetPrincipalKinds.Person, other.Id, other.DisplayName, member.Role == FleetTeamRoles.Maintainer ? "*" : "use", FleetScopeKinds.Team, team.Id,
                $"team:{team.Id}:{member.Role}"));
        }

        foreach (var grant in snapshot.Grants.Where(grant => grant.Status == FleetGrantStatuses.Active && (grant.ExpiresAt is null || grant.ExpiresAt > now)))
            rows.Add(new(grant.SubjectKind, grant.SubjectId, SubjectName(snapshot, grant.SubjectKind, grant.SubjectId), grant.Capability, grant.ScopeKind, grant.ScopeId,
                "grant:" + grant.Id));
        foreach (var owner in snapshot.Owners)
            rows.Add(new(owner.OwnerKind, owner.OwnerId, SubjectName(snapshot, owner.OwnerKind, owner.OwnerId), "*", FleetScopeKinds.Resource,
                owner.ResourceKind + ":" + owner.ResourceId, "owner"));
        return new AccessReviewDto(now, rows);
    }

    /// <summary>Records a change of access an agent proposes for its person; a person applies or dismisses it on People and access.</summary>
    /// <param name="actor">The calling controller device, for the session's person.</param>
    /// <param name="request">The proposal.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The proposal.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device, or the proposal does not hold.</exception>
    public async Task<AccessProposalDto> ProposeAsync(DeviceRecord actor, CreateAccessProposalRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        var (_, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request is null || !FleetAccessProposalKinds.IsKnown(request.Kind)) throw Invalid("kind is role, team-member, grant, transfer or team.");
        var summary = request.Summary?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > MaxSummaryLength || summary.Any(char.IsControl)) throw Invalid($"summary is 1 to {MaxSummaryLength} characters on one line.");
        if (string.IsNullOrEmpty(request.Payload) || request.Payload.Length > MaxPayloadLength || !IsJsonObject(request.Payload)) throw Invalid("payload is a JSON object.");
        if (request.SessionId is { Length: > 128 }) throw Invalid("sessionId is at most 128 characters.");
        var proposal = new AccessProposalRecord(SetupService.NewId(), actor.OrganizationId, person.Id, request.SessionId, request.Kind, summary, request.Payload,
            FleetAccessProposalStates.Open, _time.GetUtcNow(), null, null);
        await _access.AddProposalAsync(proposal, origin.Audit("access.propose", actor, proposal.Id, details: Json(("kind", proposal.Kind), ("session", proposal.SessionId ?? ""))),
            cancellationToken).ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return ToDto(proposal);
    }

    /// <summary>Lists the proposals: all for those who manage access, a person's own for others.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The proposals, the newest first.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<AccessListDto<AccessProposalDto>> ListProposalsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var all = AccessPolicy.Allows(snapshot, person.Id, "access.manage", AccessTarget.Organization, _time.GetUtcNow());
        var proposals = await _access.ListProposalsAsync(actor.OrganizationId, MaxListed, cancellationToken).ConfigureAwait(false);
        return new([.. proposals.Where(proposal => all || proposal.PersonId == person.Id).Select(ToDto)]);
    }

    /// <summary>
    /// Records that a person applied or dismissed a proposal: the console applies the change itself, with the person's own
    /// request, then records it here.
    /// </summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="proposalId">The proposal.</param>
    /// <param name="request">Applied or dismissed.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The proposal.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the proposal is not open.</exception>
    public async Task<AccessProposalDto> DecideProposalAsync(DeviceRecord actor, string proposalId, DecideAccessProposalRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request?.State is not (FleetAccessProposalStates.Applied or FleetAccessProposalStates.Dismissed)) throw Invalid("state is applied or dismissed.");
        var proposals = await _access.ListProposalsAsync(actor.OrganizationId, MaxListed, cancellationToken).ConfigureAwait(false);
        var proposal = proposals.FirstOrDefault(other => other.Id == proposalId) ?? throw NotFound("No proposal has this id.");
        if (proposal.PersonId != person.Id && !AccessPolicy.Allows(snapshot, person.Id, "access.manage", AccessTarget.Organization, _time.GetUtcNow()))
            throw Denied("A proposal is decided by its person or by those who manage access.");
        var decided = await _access.DecideProposalAsync(proposalId, request.State, person.Id, _time.GetUtcNow(),
            origin.Audit("access.proposal." + request.State, actor, proposalId, details: Json(("kind", proposal.Kind))), cancellationToken).ConfigureAwait(false)
            ?? throw CoordinatorException.Conflict(CoordinatorErrorCodes.AccessConflict, "The proposal was decided already.");
        Changed(actor.OrganizationId);
        return ToDto(decided);
    }

    /// <summary>Requires that the calling device's person holds a capability at the organization's scope, such as <c>audit.read</c>.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="capability">The capability.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once checked.</returns>
    /// <exception cref="CoordinatorException">The caller does not hold it.</exception>
    public async Task RequireOrganizationAsync(DeviceRecord actor, string capability, CancellationToken cancellationToken)
    {
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        Require(snapshot, person, capability, AccessTarget.Organization);
    }

    /// <summary>Gets whether a controller device may revoke a device of another person: it manages that machine (an owner, an admin, a maintainer of its team) or the organization.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="device">The device to revoke.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether it may.</returns>
    public async Task<bool> MayRevokeAsync(DeviceRecord actor, DeviceRecord device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        AccessSnapshot snapshot;
        PersonRecord person;
        try
        {
            (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        }
        catch (CoordinatorException)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        if (AccessPolicy.Allows(snapshot, person.Id, "access.manage", AccessTarget.Organization, now)) return true;
        return device.Kind == FleetDeviceKinds.Node && snapshot.Owner(FleetResourceKinds.Machine, device.Id) is { OwnerKind: FleetPrincipalKinds.Team } owner
            && AccessPolicy.Allows(snapshot, person.Id, "machine.manage", AccessTarget.Resource(FleetResourceKinds.Machine, device.Id, owner.OwnerKind, owner.OwnerId), now);
    }

    /// <summary>Reads the access of the organization and the calling device's person, who must be an active person on a controller device.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The snapshot and the person.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of an active person.</exception>
    internal async Task<(AccessSnapshot Snapshot, PersonRecord Person)> ReadAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is null) throw Denied("Only a person's controller device manages access.");
        var snapshot = await _access.ReadAccessAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var person = snapshot.Person(actor.OwnerPersonId) ?? throw Denied("The person of this device is not of the organization.");
        if (!person.IsActive) throw Denied("This person is suspended.");
        return (snapshot, person);
    }

    /// <summary>Gets what lists the directory per caller, which reads access again once it changed; null for none.</summary>
    public DirectoryVisibility? Visibility { get; init; }

    /// <summary>
    /// Gives a person the role the groups of their company sign-in map them to (<c>doc/fleet/identity.md</c>, company SSO): never
    /// an owner's role, which the mapper keeps, and never the last owner's. Machines follow it once a signer signs the access document.
    /// </summary>
    /// <param name="person">The person who signed in.</param>
    /// <param name="role">The role their groups give.</param>
    /// <param name="audit">The audit record of the change, as the sign-in.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>Whether the role changed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is the owner role or no role.</exception>
    public async Task<bool> ApplyGroupRoleAsync(PersonRecord person, string role, AuditEntry audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(audit);
        if (!FleetOrganizationRoles.IsKnown(role) || role == FleetOrganizationRoles.Owner) throw new ArgumentException("Groups give a role other than owner.", nameof(role));
        if (person.Role == FleetOrganizationRoles.Owner || person.Role == role) return false;
        var outcome = await _access.SetPersonRoleAsync(person.OrganizationId, person.Id, role, audit, cancellationToken).ConfigureAwait(false);
        if (outcome != RoleChangeOutcome.Changed) return false;
        Changed(person.OrganizationId);
        return true;
    }

    /// <summary>Tells the devices of the organization that access changed, and has the directory read access again.</summary>
    /// <param name="organizationId">The organization.</param>
    internal void Changed(string organizationId)
    {
        Visibility?.Invalidate(organizationId);
        _notifications.TellOrganization(organizationId, FleetAccessMethods.Changed);
    }

    internal void Require(AccessSnapshot snapshot, PersonRecord person, string capability, AccessTarget target)
    {
        var decision = AccessPolicy.Decide(snapshot, person.Id, capability, target, _time.GetUtcNow());
        if (!decision.Allowed) throw Denied($"This needs '{capability}', which {person.DisplayName} does not hold here.");
    }

    internal static CoordinatorException Denied(string message) => CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, message);

    internal static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.AccessInvalid, message);

    internal static CoordinatorException NotFound(string message) => CoordinatorException.NotFound(CoordinatorErrorCodes.AccessNotFound, message);

    internal static CoordinatorException LastOwner()
        => CoordinatorException.Conflict(CoordinatorErrorCodes.LastOwner, "The organization keeps an active owner: make another person owner first.");

    internal static string ManageCapability(string resourceKind) => resourceKind switch
    {
        FleetResourceKinds.Automation => "automation.manage-access",
        FleetResourceKinds.Session => "session.share",
        FleetResourceKinds.Machine => "machine.manage",
        _ => "access.manage",
    };

    internal static bool IsAdvanced(AccessSnapshot snapshot)
        => snapshot.Teams.Any(static team => team.DissolvedAt is null) || snapshot.Grants.Count > 0 || snapshot.ServiceIdentities.Count > 0
            || snapshot.People.Any(static person => person.Role is not (FleetOrganizationRoles.Owner or FleetOrganizationRoles.Member));

    internal static string SubjectName(AccessSnapshot snapshot, string kind, string id) => kind switch
    {
        FleetPrincipalKinds.Person => snapshot.Person(id)?.DisplayName ?? id,
        FleetPrincipalKinds.Team => snapshot.Teams.FirstOrDefault(team => team.Id == id)?.Name ?? id,
        FleetPrincipalKinds.Service => snapshot.ServiceIdentities.FirstOrDefault(identity => identity.Id == id)?.Name ?? id,
        FleetPrincipalKinds.Organization => "Organization",
        _ => id,
    };

    internal static PersonAccessDto ToDto(AccessSnapshot snapshot, PersonRecord person, IReadOnlyList<DeviceRecord> devices)
        => new(person.Id, person.DisplayName, person.Role, person.Status, person.CreatedAt, person.SuspendedAt,
            devices.Count(device => device.OwnerPersonId == person.Id && device.Kind == FleetDeviceKinds.Controller && device.Status == DeviceStatuses.Approved),
            [.. snapshot.Members.Where(member => member.PersonId == person.Id && snapshot.Team(member.TeamId) is not null)
                .Select(member => new TeamMembershipDto(member.TeamId, snapshot.Team(member.TeamId)!.Name, member.Role))]);

    internal static TeamDto ToDto(AccessSnapshot snapshot, TeamRecord team)
        => new(team.Id, team.Name, team.CreatedAt,
            [.. snapshot.Members.Where(member => member.TeamId == team.Id).Select(member => new TeamMemberDto(member.PersonId, snapshot.Person(member.PersonId)?.DisplayName ?? member.PersonId,
                member.Role))],
            [.. snapshot.Owners.Where(owner => owner.ResourceKind == FleetResourceKinds.Machine && owner.OwnerKind == FleetPrincipalKinds.Team && owner.OwnerId == team.Id)
                .Select(static owner => owner.ResourceId).Order(StringComparer.Ordinal)],
            team.RunsMemberAutomations, team.Version);

    internal static ServiceIdentityDto ToDto(ServiceIdentityRecord identity)
        => new(identity.Id, identity.Name, identity.OwnerKind, identity.OwnerId, identity.CreatedAt, identity.Enabled);

    internal static GrantDto ToDto(GrantRecord grant)
        => new(grant.Id, grant.SubjectKind, grant.SubjectId, grant.Capability, grant.ScopeKind, grant.ScopeId, grant.Status, grant.CreatedAt, grant.CreatedByPersonId, grant.ExpiresAt);

    internal static TransferDto ToDto(TransferRecord transfer, string? name)
        => new(transfer.Id, transfer.ResourceKind, transfer.ResourceId, name, transfer.FromKind, transfer.FromId, transfer.ToKind, transfer.ToId, transfer.State, transfer.Reason,
            transfer.OfferedByPersonId, transfer.OfferedAt, transfer.DecidedByPersonId, transfer.DecidedAt, transfer.HasHandover);

    internal static AccessProposalDto ToDto(AccessProposalRecord proposal)
        => new(proposal.Id, proposal.PersonId, proposal.SessionId, proposal.Kind, proposal.Summary, proposal.Payload, proposal.State, proposal.CreatedAt,
            proposal.DecidedByPersonId, proposal.DecidedAt);

    internal static string Json(params (string Name, string Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields) writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string GrantDetails(GrantRecord grant)
        => Json(("subject", grant.SubjectKind + ":" + grant.SubjectId), ("capability", grant.Capability), ("scope", grant.ScopeKind + ":" + (grant.ScopeId ?? "")),
            ("status", grant.Status));

    internal static string TransferDetails(TransferRecord transfer)
        => Json(("resource", transfer.ResourceKind + ":" + transfer.ResourceId), ("from", transfer.FromKind + ":" + transfer.FromId), ("to", transfer.ToKind + ":" + transfer.ToId),
            ("reason", transfer.Reason));

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<PersonAccessDto> PersonAsync(string organizationId, string personId, CancellationToken cancellationToken)
    {
        var snapshot = await _access.ReadAccessAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var devices = await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return ToDto(snapshot, snapshot.Person(personId) ?? throw NotFound("No person of the organization has this id."), devices);
    }

    private async Task<TeamDto> TeamAsync(string organizationId, string teamId, CancellationToken cancellationToken)
    {
        var snapshot = await _access.ReadAccessAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return ToDto(snapshot, snapshot.Team(teamId) ?? throw NotFound("No team has this id."));
    }

    // The target a grant's scope names: a team, a project, the organization, or a resource with its owner.
    private async Task<AccessTarget> GrantTargetAsync(string organizationId, AccessSnapshot snapshot, string scopeKind, string? scopeId, CancellationToken cancellationToken)
    {
        switch (scopeKind)
        {
            case FleetScopeKinds.Organization:
                return AccessTarget.Organization;
            case FleetScopeKinds.Team:
                _ = snapshot.Team(scopeId) ?? throw NotFound("No team has this id.");
                return AccessTarget.Team(scopeId!);
            case FleetScopeKinds.Project:
                if (string.IsNullOrWhiteSpace(scopeId) || scopeId.Length > 512 || scopeId.Any(char.IsControl)) throw Invalid("A project is named by its repository key or blind index.");
                return new AccessTarget(FleetScopeKinds.Project, null, scopeId, null, null, null, null);
            default:
                var colon = scopeId?.IndexOf(':') ?? -1;
                if (colon <= 0 || colon == scopeId!.Length - 1) throw Invalid("A resource is named <kind>:<id>.");
                var (kind, id) = (scopeId[..colon], scopeId[(colon + 1)..]);
                if (!FleetResourceKinds.IsKnown(kind)) throw Invalid($"'{kind}' is not a kind of resource.");
                var owner = await OwnerOfAsync(organizationId, snapshot, kind, id, cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound("No resource of the organization has this id.");
                return AccessTarget.Resource(kind, id, owner.Kind, owner.Id);
        }
    }
}
