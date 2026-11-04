using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>The events of access control on the control connection.</summary>
public static class FleetAccessMethods
{
    /// <summary>
    /// The event a server sends every device when people, teams, roles, grants, owners or the signed access document
    /// changed: devices read what they need again (nodes, the signed document, which they verify themselves).
    /// </summary>
    public const string Changed = "access.changed";
}

/// <summary>A person's membership of a team.</summary>
/// <param name="TeamId">The team.</param>
/// <param name="TeamName">Its name.</param>
/// <param name="Role">The person's role in it (<see cref="FleetTeamRoles"/>).</param>
public sealed record TeamMembershipDto(string TeamId, string TeamName, string Role);

/// <summary>A person as People and access shows them.</summary>
/// <param name="Id">Their id.</param>
/// <param name="DisplayName">Their name.</param>
/// <param name="Role">Their organization role (<see cref="FleetOrganizationRoles"/>).</param>
/// <param name="Status">Whether they act (<see cref="FleetPersonStatuses"/>).</param>
/// <param name="CreatedAt">When they joined.</param>
/// <param name="SuspendedAt">When they were suspended; null while they act.</param>
/// <param name="Devices">How many approved devices they have; 0 for a person invited who has not joined yet.</param>
/// <param name="Teams">Their teams.</param>
public sealed record PersonAccessDto(string Id, string DisplayName, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset? SuspendedAt, int Devices,
    IReadOnlyList<TeamMembershipDto> Teams);

/// <summary>A member of a team.</summary>
/// <param name="PersonId">The person.</param>
/// <param name="DisplayName">Their name.</param>
/// <param name="Role">Their role in the team (<see cref="FleetTeamRoles"/>).</param>
public sealed record TeamMemberDto(string PersonId, string DisplayName, string Role);

/// <summary>A team.</summary>
/// <param name="Id">The team.</param>
/// <param name="Name">Its name.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="Members">Its maintainers and members.</param>
/// <param name="Machines">The devices of its team machines.</param>
/// <param name="RunsMemberAutomations">Whether its machines run the personal automations of its members (off by default).</param>
/// <param name="Version">The version of the team, raised by each change.</param>
public sealed record TeamDto(string Id, string Name, DateTimeOffset CreatedAt, IReadOnlyList<TeamMemberDto> Members, IReadOnlyList<string> Machines, bool RunsMemberAutomations,
    long Version);

/// <summary>A service identity: the identity a team or the organization runs unattended work as; never a password or a subscription.</summary>
/// <param name="Id">The identity.</param>
/// <param name="Name">Its name.</param>
/// <param name="OwnerKind">Who it belongs to: <c>team</c> or <c>organization</c>.</param>
/// <param name="OwnerId">The team, or the organization.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="Enabled">Whether it may act: disabled until a person enables it.</param>
public sealed record ServiceIdentityDto(string Id, string Name, string OwnerKind, string OwnerId, DateTimeOffset CreatedAt, bool Enabled);

/// <summary>A grant <c>(subject, capability, scope)</c>.</summary>
/// <param name="Id">The grant.</param>
/// <param name="SubjectKind">Who is granted (<see cref="FleetPrincipalKinds"/>: a person, a team or a service identity).</param>
/// <param name="SubjectId">Their id.</param>
/// <param name="Capability">What they may do (<see cref="FleetCapabilities"/>).</param>
/// <param name="ScopeKind">Where (<see cref="FleetScopeKinds"/>).</param>
/// <param name="ScopeId">The team, the project key or <c>&lt;kind&gt;:&lt;id&gt;</c> of a resource; null for the organization.</param>
/// <param name="Status">Where it stands (<see cref="FleetGrantStatuses"/>).</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="CreatedBy">The person who made it.</param>
/// <param name="ExpiresAt">When it stops applying; null for never.</param>
public sealed record GrantDto(string Id, string SubjectKind, string SubjectId, string Capability, string ScopeKind, string? ScopeId, string Status, DateTimeOffset CreatedAt,
    string CreatedBy, DateTimeOffset? ExpiresAt)
{
    /// <summary>Gets whether its grantee has a device its copies of the key miss: a computer of the owner of the content keys it again.</summary>
    public bool MissingKeys { get; init; }
}

/// <summary>The owner of a resource, who wrote it, and whether it waits for its owner.</summary>
/// <param name="ResourceKind">The kind of resource (<see cref="FleetResourceKinds"/>).</param>
/// <param name="ResourceId">Its id.</param>
/// <param name="Name">Its name as the server knows it; null when it is sealed.</param>
/// <param name="OwnerKind">Who answers for it now (<see cref="FleetPrincipalKinds"/>).</param>
/// <param name="OwnerId">Their id.</param>
/// <param name="OwnerName">Their name.</param>
/// <param name="AuthorId">Who made it, a person; immutable history.</param>
/// <param name="AuthorSession">The agent session that made it for that person, if one did.</param>
/// <param name="Paused">Whether it waits for its owner to resume it (after a transfer, or its owner leaving).</param>
/// <param name="OwnerSuspended">Whether its owner is a suspended person: the console lists it until someone takes it.</param>
public sealed record ResourceOwnerDto(string ResourceKind, string ResourceId, string? Name, string OwnerKind, string OwnerId, string OwnerName, string? AuthorId,
    string? AuthorSession, bool Paused, bool OwnerSuspended);

/// <summary>A transfer of a resource to another owner.</summary>
/// <param name="Id">The transfer.</param>
/// <param name="ResourceKind">The kind of resource.</param>
/// <param name="ResourceId">Its id.</param>
/// <param name="ResourceName">Its name, when the server knows one.</param>
/// <param name="FromKind">The owner it leaves.</param>
/// <param name="FromId">Their id.</param>
/// <param name="ToKind">The owner it goes to.</param>
/// <param name="ToId">Their id.</param>
/// <param name="State">Where it stands (<see cref="FleetTransferStates"/>).</param>
/// <param name="Reason">Why (<see cref="FleetTransferReasons"/>).</param>
/// <param name="OfferedBy">The person who offered it.</param>
/// <param name="OfferedAt">When.</param>
/// <param name="DecidedBy">The person who accepted or declined it.</param>
/// <param name="DecidedAt">When.</param>
/// <param name="HasHandover">Whether a computer of its former owner sealed what it does for the devices of who takes it.</param>
public sealed record TransferDto(string Id, string ResourceKind, string ResourceId, string? ResourceName, string FromKind, string FromId, string ToKind, string ToId, string State,
    string Reason, string OfferedBy, DateTimeOffset OfferedAt, string? DecidedBy, DateTimeOffset? DecidedAt, bool HasHandover = false);

/// <summary>
/// The hand-over of an automation given to a person (<c>doc/fleet/access-control.md</c>, ownership over time): what it does,
/// sealed by a computer of its owner with a key made for the transfer, and that key wrapped to each device of the person who
/// takes it. The server stores it without opening it.
/// </summary>
/// <param name="Sealed">What the automation does, sealed.</param>
/// <param name="Wraps">The copy of the key for each device of who takes it.</param>
public sealed record TransferHandoverDto(string Sealed, IReadOnlyList<GrantKeyWrapDto> Wraps);

/// <summary>
/// A provider of a team or of the organization (<c>doc/fleet/orchestration.md</c>, credentials of unattended runs): defined
/// once by who answers for its scope, with the key of an account sealed for the machines of that scope only.
/// </summary>
/// <param name="Id">Its id, which is its provider key on the machines.</param>
/// <param name="OwnerKind">A team or the organization.</param>
/// <param name="OwnerId">Its owner.</param>
/// <param name="Name">The name people see.</param>
/// <param name="Type">The type of provider, one that runs with an API key (<c>anthropic</c>, <c>openai-responses</c>...).</param>
/// <param name="Endpoint">Its endpoint, such as a gateway; null for the vendor's own.</param>
/// <param name="Models">The models it offers, the first one by default.</param>
/// <param name="Version">Its version, which a new key or definition makes.</param>
/// <param name="UpdatedBy">The person who set it last.</param>
/// <param name="UpdatedAt">When.</param>
/// <param name="Machines">The machines that hold its key.</param>
public sealed record ScopeProviderDto(string Id, string OwnerKind, string OwnerId, string Name, string Type, string? Endpoint, IReadOnlyList<string> Models, long Version,
    string UpdatedBy, DateTimeOffset UpdatedAt, IReadOnlyList<string> Machines);

/// <summary>The body of <c>PUT /api/v1/providers/{id}</c>: a provider of a scope, with its key sealed for the machines of the scope.</summary>
/// <param name="OwnerKind">A team or the organization.</param>
/// <param name="OwnerId">Its owner.</param>
/// <param name="Name">The name people see.</param>
/// <param name="Type">The type of provider.</param>
/// <param name="Endpoint">Its endpoint; null for the vendor's own.</param>
/// <param name="Models">The models it offers.</param>
/// <param name="Version">Its new version, above the one held.</param>
/// <param name="Sealed">The key of the account, sealed.</param>
/// <param name="Wraps">The copy of its sealing key for each machine of the scope.</param>
public sealed record PutScopeProviderRequest(string OwnerKind, string OwnerId, string Name, string Type, string? Endpoint, IReadOnlyList<string> Models, long Version,
    string Sealed, IReadOnlyList<GrantKeyWrapDto> Wraps);

/// <summary>What a provider of a scope gives the calling machine (<c>GET /api/v1/provider-keys</c>): its definition and this machine's copy of its key.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Sealed">Its key, sealed.</param>
/// <param name="Wrapped">This machine's copy of the sealing key.</param>
public sealed record ScopeProviderKeyDto(ScopeProviderDto Provider, string Sealed, string Wrapped);

/// <summary>What a hand-over gives the calling device (<c>GET /api/v1/transfers/{id}/handover</c>).</summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="ResourceId">The automation.</param>
/// <param name="FromPersonId">Its former owner, whose computer sealed and signed it.</param>
/// <param name="Sealed">What it does, sealed.</param>
/// <param name="Wrapped">This device's copy of the key.</param>
public sealed record TransferHandoverCopyDto(string TransferId, string ResourceId, string FromPersonId, string Sealed, string Wrapped);

/// <summary>Where the signed access document stands, as People and access shows it.</summary>
/// <param name="Number">The number of the last signed version; 0 when none was signed yet.</param>
/// <param name="SignedBy">The person whose device signed it.</param>
/// <param name="SignedAt">When.</param>
/// <param name="Pending">The changes of the server's model that machines do not see yet, for people: they wait for a signer's device.</param>
public sealed record AccessDocumentStateDto(long Number, string? SignedBy, DateTimeOffset? SignedAt, IReadOnlyList<string> Pending);

/// <summary>What the calling device's person is, and what they may do in the organization.</summary>
/// <param name="Person">The person.</param>
/// <param name="Capabilities">What they may do at the organization's scope (<see cref="FleetCapabilities"/>), such as <c>access.manage</c>.</param>
/// <param name="Advanced">Whether the organization uses teams, grants, service identities or roles beyond owner and members: the console then shows them first.</param>
/// <param name="Document">Where the signed access document stands.</param>
/// <param name="Signer">Whether this person's devices sign the access document (owners and admins).</param>
public sealed record AccessSelfDto(PersonAccessDto Person, IReadOnlyList<string> Capabilities, bool Advanced, AccessDocumentStateDto Document, bool Signer);

/// <summary>The body of <c>POST /api/v1/people</c>: invites a person, who joins with the code given back.</summary>
/// <param name="DisplayName">Their name.</param>
/// <param name="Role">Their role; <c>member</c> when absent.</param>
/// <param name="TtlSeconds">How long the code works: 10 minutes to 7 days; 3 days when absent.</param>
public sealed record InvitePersonRequest(string DisplayName, string? Role = null, int? TtlSeconds = null);

/// <summary>An invitation: the person, and the code they join with, shown once.</summary>
/// <param name="Person">The person invited.</param>
/// <param name="Token">The code their computer enrolls with, as a controller device of theirs; shown once and never stored.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
public sealed record InvitationDto(PersonAccessDto Person, string Token, DateTimeOffset ExpiresAt);

/// <summary>The body of <c>PUT /api/v1/people/{id}/role</c>.</summary>
/// <param name="Role">The new role (<see cref="FleetOrganizationRoles"/>).</param>
public sealed record SetRoleRequest(string Role);

/// <summary>The body of <c>POST /api/v1/teams</c>.</summary>
/// <param name="Name">The name of the team.</param>
public sealed record CreateTeamRequest(string Name);

/// <summary>The body of <c>PATCH /api/v1/teams/{id}</c>: what changes.</summary>
/// <param name="Name">The new name, or null.</param>
/// <param name="RunsMemberAutomations">Whether its machines run the personal automations of its members, or null.</param>
public sealed record UpdateTeamRequest(string? Name = null, bool? RunsMemberAutomations = null);

/// <summary>The body of <c>PUT /api/v1/teams/{id}/members/{personId}</c>.</summary>
/// <param name="Role">Their role in the team (<see cref="FleetTeamRoles"/>).</param>
public sealed record SetTeamMemberRequest(string Role);

/// <summary>The body of <c>POST /api/v1/service-identities</c>.</summary>
/// <param name="Name">Its name.</param>
/// <param name="OwnerKind"><c>team</c> or <c>organization</c>.</param>
/// <param name="OwnerId">The team, or the organization.</param>
public sealed record CreateServiceIdentityRequest(string Name, string OwnerKind, string OwnerId);

/// <summary>The body of <c>POST /api/v1/grants</c>.</summary>
/// <param name="SubjectKind">Who is granted.</param>
/// <param name="SubjectId">Their id.</param>
/// <param name="Capability">What they may do.</param>
/// <param name="ScopeKind">Where.</param>
/// <param name="ScopeId">The team, project key or <c>&lt;kind&gt;:&lt;id&gt;</c>; null for the organization.</param>
/// <param name="ExpiresAt">When it stops applying; null for never.</param>
/// <param name="Sealed">Whether the content it opens is sealed from the server (a Private space): it waits for a device that holds the keys.</param>
public sealed record CreateGrantRequest(string SubjectKind, string SubjectId, string Capability, string ScopeKind, string? ScopeId = null, DateTimeOffset? ExpiresAt = null,
    bool Sealed = false);

/// <summary>The body of <c>POST /api/v1/transfers</c>: offers a resource to another owner.</summary>
/// <param name="ResourceKind">The kind of resource (<see cref="FleetResourceKinds.Transfers"/>).</param>
/// <param name="ResourceId">Its id.</param>
/// <param name="ToKind">A person, a team or the organization.</param>
/// <param name="ToId">Their id.</param>
/// <param name="Handover">For an automation given to a person: what it does, sealed for their devices; null for none.</param>
public sealed record OfferTransferRequest(string ResourceKind, string ResourceId, string ToKind, string ToId, TransferHandoverDto? Handover = null);

/// <summary>The body of <c>POST /api/v1/people/{id}/deprovision</c>.</summary>
/// <param name="SuccessorKind">Who takes what they owned in team and organization scopes: a person or a team; null for the organization.</param>
/// <param name="SuccessorId">Their id.</param>
public sealed record DeprovisionRequest(string? SuccessorKind = null, string? SuccessorId = null);

/// <summary>What a deprovisioning did, and what the console still signs.</summary>
/// <param name="Person">The person, suspended.</param>
/// <param name="Transfers">What went to the successor, paused until they resume it.</param>
/// <param name="DevicesToRevoke">Their approved devices: the console signs the entries that remove them from the device list.</param>
/// <param name="MachinesLeaving">Their personal machines, among those devices: they leave the fleet.</param>
public sealed record DeprovisionResultDto(PersonAccessDto Person, IReadOnlyList<TransferDto> Transfers, IReadOnlyList<string> DevicesToRevoke, IReadOnlyList<string> MachinesLeaving);

/// <summary>A line of "who can do what": a principal, a capability, a scope and where it comes from.</summary>
/// <param name="PrincipalKind">A person, a team or a service identity.</param>
/// <param name="PrincipalId">Their id.</param>
/// <param name="PrincipalName">Their name.</param>
/// <param name="Capability">The capability, or <c>*</c> for every management capability of a role.</param>
/// <param name="ScopeKind">Where.</param>
/// <param name="ScopeId">The scope's id, or null for the organization.</param>
/// <param name="Source">Where it comes from: <c>role:admin</c>, <c>team:&lt;id&gt;:maintainer</c>, <c>grant:&lt;id&gt;</c>, <c>owner</c>.</param>
public sealed record AccessReviewRowDto(string PrincipalKind, string PrincipalId, string PrincipalName, string Capability, string ScopeKind, string? ScopeId, string Source);

/// <summary>Who can do what, for an access review.</summary>
/// <param name="At">When it was read.</param>
/// <param name="Rows">The lines.</param>
public sealed record AccessReviewDto(DateTimeOffset At, IReadOnlyList<AccessReviewRowDto> Rows);

/// <summary>The kinds of change an agent may propose for a person to apply (<c>doc/fleet/access-control.md</c>, agents and access).</summary>
public static class FleetAccessProposalKinds
{
    /// <summary>A role for a person: the payload is <c>{ "personId", "role" }</c>.</summary>
    public const string Role = "role";

    /// <summary>A person in a team: <c>{ "teamId", "personId", "role" }</c>, or without role to take them out.</summary>
    public const string TeamMember = "team-member";

    /// <summary>A grant: the payload is a <see cref="CreateGrantRequest"/>.</summary>
    public const string Grant = "grant";

    /// <summary>A transfer: the payload is an <see cref="OfferTransferRequest"/>.</summary>
    public const string Transfer = "transfer";

    /// <summary>A new team: the payload is a <see cref="CreateTeamRequest"/>.</summary>
    public const string Team = "team";

    /// <summary>Gets whether a text is a kind of proposal.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it is known.</returns>
    public static bool IsKnown(string? kind) => kind is Role or TeamMember or Grant or Transfer or Team;
}

/// <summary>Where a proposal stands.</summary>
public static class FleetAccessProposalStates
{
    /// <summary>Waiting for a person.</summary>
    public const string Open = "open";

    /// <summary>A person applied it.</summary>
    public const string Applied = "applied";

    /// <summary>A person dismissed it.</summary>
    public const string Dismissed = "dismissed";
}

/// <summary>A change of access an agent proposed; a person applies it or dismisses it.</summary>
/// <param name="Id">The proposal.</param>
/// <param name="PersonId">The person the agent acts for.</param>
/// <param name="SessionId">The session that proposed it.</param>
/// <param name="Kind">What it changes (<see cref="FleetAccessProposalKinds"/>).</param>
/// <param name="Summary">What it does and why, in a sentence.</param>
/// <param name="Payload">The change, as the JSON of its request.</param>
/// <param name="State">Where it stands (<see cref="FleetAccessProposalStates"/>).</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="DecidedBy">The person who applied or dismissed it.</param>
/// <param name="DecidedAt">When.</param>
public sealed record AccessProposalDto(string Id, string PersonId, string? SessionId, string Kind, string Summary, string Payload, string State, DateTimeOffset CreatedAt,
    string? DecidedBy, DateTimeOffset? DecidedAt);

/// <summary>The body of <c>POST /api/v1/access/proposals</c>.</summary>
/// <param name="Kind">What it changes.</param>
/// <param name="Summary">What it does and why.</param>
/// <param name="Payload">The change, as the JSON of its request.</param>
/// <param name="SessionId">The session that proposes it.</param>
public sealed record CreateAccessProposalRequest(string Kind, string Summary, string Payload, string? SessionId = null);

/// <summary>The body of <c>POST /api/v1/access/proposals/{id}/decide</c>.</summary>
/// <param name="State"><c>applied</c> once the person applied it, or <c>dismissed</c>.</param>
public sealed record DecideAccessProposalRequest(string State);

/// <summary>The copy of the key of a grant of sealed content for one device of the grantee.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Wrapped">The key, wrapped to that device and signed by the device that gives it.</param>
public sealed record GrantKeyWrapDto(string DeviceId, string Wrapped);

/// <summary>
/// The body of <c>POST /api/v1/grants/{id}/keys</c> for a grant of sealed session content: the metadata of the session sealed with
/// a key made for the grant, and that key wrapped to each device of the grantee, by a device of the owner of the session.
/// </summary>
/// <param name="Sealed">The sealed metadata, which the server cannot open.</param>
/// <param name="Wraps">The copies of its key.</param>
public sealed record ConfirmGrantKeysRequest(string Sealed, IReadOnlyList<GrantKeyWrapDto> Wraps);

/// <summary>What a grant of sealed content gives the calling device: the sealed metadata of a session and the copy of its key for that device.</summary>
/// <param name="GrantId">The grant.</param>
/// <param name="SessionId">The session.</param>
/// <param name="MachineId">The machine of the session, whose owner gave the key.</param>
/// <param name="Sealed">The sealed metadata.</param>
/// <param name="Wrapped">The copy of its key for the calling device.</param>
public sealed record GrantKeyDto(string GrantId, string SessionId, string MachineId, string Sealed, string Wrapped);

/// <summary>A list of items of access control.</summary>
/// <typeparam name="T">The type of the items.</typeparam>
/// <param name="Items">The items.</param>
public sealed record AccessListDto<T>(IReadOnlyList<T> Items);

/// <summary>The source-generated contracts of access control on the REST API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PersonAccessDto))]
[JsonSerializable(typeof(AccessListDto<PersonAccessDto>))]
[JsonSerializable(typeof(TeamDto))]
[JsonSerializable(typeof(AccessListDto<TeamDto>))]
[JsonSerializable(typeof(ServiceIdentityDto))]
[JsonSerializable(typeof(AccessListDto<ServiceIdentityDto>))]
[JsonSerializable(typeof(GrantDto))]
[JsonSerializable(typeof(AccessListDto<GrantDto>))]
[JsonSerializable(typeof(ResourceOwnerDto))]
[JsonSerializable(typeof(AccessListDto<ResourceOwnerDto>))]
[JsonSerializable(typeof(TransferDto))]
[JsonSerializable(typeof(AccessListDto<TransferDto>))]
[JsonSerializable(typeof(AccessProposalDto))]
[JsonSerializable(typeof(AccessListDto<AccessProposalDto>))]
[JsonSerializable(typeof(AccessSelfDto))]
[JsonSerializable(typeof(AccessReviewDto))]
[JsonSerializable(typeof(InvitePersonRequest))]
[JsonSerializable(typeof(InvitationDto))]
[JsonSerializable(typeof(SetRoleRequest))]
[JsonSerializable(typeof(CreateTeamRequest))]
[JsonSerializable(typeof(UpdateTeamRequest))]
[JsonSerializable(typeof(SetTeamMemberRequest))]
[JsonSerializable(typeof(CreateServiceIdentityRequest))]
[JsonSerializable(typeof(CreateGrantRequest))]
[JsonSerializable(typeof(OfferTransferRequest))]
[JsonSerializable(typeof(DeprovisionRequest))]
[JsonSerializable(typeof(DeprovisionResultDto))]
[JsonSerializable(typeof(CreateAccessProposalRequest))]
[JsonSerializable(typeof(DecideAccessProposalRequest))]
[JsonSerializable(typeof(ConfirmGrantKeysRequest))]
[JsonSerializable(typeof(TransferHandoverCopyDto))]
[JsonSerializable(typeof(PutScopeProviderRequest))]
[JsonSerializable(typeof(ScopeProviderDto))]
[JsonSerializable(typeof(AccessListDto<ScopeProviderDto>))]
[JsonSerializable(typeof(AccessListDto<ScopeProviderKeyDto>))]
[JsonSerializable(typeof(AccessListDto<GrantKeyDto>))]
[JsonSerializable(typeof(ApiProblemDto))]
public sealed partial class FleetAccessApiJsonContext : JsonSerializerContext;
