namespace CodeAlta.Server.Storage;

/// <summary>A team of the organization.</summary>
/// <param name="Id">The team.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="Name">Its name.</param>
/// <param name="RunsMemberAutomations">Whether its machines run the personal automations of its members.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="CreatedByPersonId">Who made it.</param>
/// <param name="DissolvedAt">When it was dissolved; null while it lives.</param>
/// <param name="Version">The version of the record, raised by each change.</param>
public sealed record TeamRecord(string Id, string OrganizationId, string Name, bool RunsMemberAutomations, DateTimeOffset CreatedAt, string CreatedByPersonId,
    DateTimeOffset? DissolvedAt, long Version);

/// <summary>A person's role in a team.</summary>
/// <param name="TeamId">The team.</param>
/// <param name="PersonId">The person.</param>
/// <param name="Role">Their role (<see cref="CodeAlta.Fleet.Protocol.FleetTeamRoles"/>).</param>
/// <param name="AddedAt">When they were added.</param>
public sealed record TeamMemberRecord(string TeamId, string PersonId, string Role, DateTimeOffset AddedAt);

/// <summary>A service identity of a team or of the organization.</summary>
/// <param name="Id">The identity.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="Name">Its name.</param>
/// <param name="OwnerKind"><c>team</c> or <c>organization</c>.</param>
/// <param name="OwnerId">The team or the organization.</param>
/// <param name="Enabled">Whether it may act.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="CreatedByPersonId">Who made it.</param>
public sealed record ServiceIdentityRecord(string Id, string OrganizationId, string Name, string OwnerKind, string OwnerId, bool Enabled, DateTimeOffset CreatedAt,
    string CreatedByPersonId);

/// <summary>A grant <c>(subject, capability, scope)</c>.</summary>
/// <param name="Id">The grant.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="SubjectKind">A person, a team or a service identity.</param>
/// <param name="SubjectId">Their id.</param>
/// <param name="Capability">The capability.</param>
/// <param name="ScopeKind">The scope.</param>
/// <param name="ScopeId">Its id; null for the organization.</param>
/// <param name="Status">Where it stands (<see cref="CodeAlta.Fleet.Protocol.FleetGrantStatuses"/>).</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="CreatedByPersonId">Who made it.</param>
/// <param name="ExpiresAt">When it stops applying.</param>
/// <param name="RevokedAt">When it was taken back.</param>
public sealed record GrantRecord(string Id, string OrganizationId, string SubjectKind, string SubjectId, string Capability, string ScopeKind, string? ScopeId, string Status,
    DateTimeOffset CreatedAt, string CreatedByPersonId, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);

/// <summary>The owner of a resource where it is not the one its record implies, who made it, and whether it waits for its owner.</summary>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="ResourceKind">The kind of resource.</param>
/// <param name="ResourceId">Its id.</param>
/// <param name="OwnerKind">A person, a team or the organization.</param>
/// <param name="OwnerId">Their id.</param>
/// <param name="AuthorPersonId">Who made it.</param>
/// <param name="AuthorSessionId">The agent session that made it for that person.</param>
/// <param name="Paused">Whether it waits for its owner to resume it.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record ResourceOwnerRecord(string OrganizationId, string ResourceKind, string ResourceId, string OwnerKind, string OwnerId, string? AuthorPersonId,
    string? AuthorSessionId, bool Paused, DateTimeOffset UpdatedAt);

/// <summary>A transfer of a resource to another owner.</summary>
/// <param name="Id">The transfer.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="ResourceKind">The kind of resource.</param>
/// <param name="ResourceId">Its id.</param>
/// <param name="FromKind">The owner it leaves.</param>
/// <param name="FromId">Their id.</param>
/// <param name="ToKind">The owner it goes to.</param>
/// <param name="ToId">Their id.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Reason">Why.</param>
/// <param name="OfferedByPersonId">Who offered it.</param>
/// <param name="OfferedAt">When.</param>
/// <param name="DecidedByPersonId">Who accepted or declined it.</param>
/// <param name="DecidedAt">When.</param>
public sealed record TransferRecord(string Id, string OrganizationId, string ResourceKind, string ResourceId, string FromKind, string FromId, string ToKind, string ToId,
    string State, string Reason, string OfferedByPersonId, DateTimeOffset OfferedAt, string? DecidedByPersonId, DateTimeOffset? DecidedAt)
{
    /// <summary>Gets whether a hand-over of what the resource does goes with it.</summary>
    public bool HasHandover { get; init; }
}

/// <summary>A version of the signed access document, as stored.</summary>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="Number">Its number, from 1.</param>
/// <param name="Payload">The JSON text that was signed.</param>
/// <param name="Signature">The signature.</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="Hash">Its hash, which the next version names.</param>
/// <param name="RecordedAt">When the server recorded it.</param>
public sealed record AccessDocumentRecord(string OrganizationId, long Number, string Payload, string Signature, string SignerDeviceId, string Hash, DateTimeOffset RecordedAt);

/// <summary>A change of access an agent proposed.</summary>
/// <param name="Id">The proposal.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="PersonId">The person the agent acts for.</param>
/// <param name="SessionId">The session that proposed it.</param>
/// <param name="Kind">What it changes.</param>
/// <param name="Summary">What it does and why.</param>
/// <param name="Payload">The change, as JSON.</param>
/// <param name="State">Where it stands.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="DecidedByPersonId">Who applied or dismissed it.</param>
/// <param name="DecidedAt">When.</param>
public sealed record AccessProposalRecord(string Id, string OrganizationId, string PersonId, string? SessionId, string Kind, string Summary, string Payload, string State,
    DateTimeOffset CreatedAt, string? DecidedByPersonId, DateTimeOffset? DecidedAt);

/// <summary>Everything that decides access in an organization, read at once: small, and read again at each decision.</summary>
/// <param name="People">The people.</param>
/// <param name="Teams">The teams, dissolved ones included.</param>
/// <param name="Members">The members of the teams.</param>
/// <param name="Grants">The grants that are not revoked.</param>
/// <param name="ServiceIdentities">The service identities.</param>
/// <param name="Owners">The owners of resources that have one of their own.</param>
public sealed record AccessSnapshot(
    IReadOnlyList<PersonRecord> People,
    IReadOnlyList<TeamRecord> Teams,
    IReadOnlyList<TeamMemberRecord> Members,
    IReadOnlyList<GrantRecord> Grants,
    IReadOnlyList<ServiceIdentityRecord> ServiceIdentities,
    IReadOnlyList<ResourceOwnerRecord> Owners)
{
    /// <summary>Finds a person.</summary>
    /// <param name="personId">The person.</param>
    /// <returns>The person, or null.</returns>
    public PersonRecord? Person(string? personId) => personId is null ? null : People.FirstOrDefault(person => person.Id == personId);

    /// <summary>Finds a living team.</summary>
    /// <param name="teamId">The team.</param>
    /// <returns>The team, or null when it is unknown or dissolved.</returns>
    public TeamRecord? Team(string? teamId) => teamId is null ? null : Teams.FirstOrDefault(team => team.Id == teamId && team.DissolvedAt is null);

    /// <summary>Gets a person's role in a team.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="personId">The person.</param>
    /// <returns>Their role, or null when they are not in it.</returns>
    public string? TeamRole(string teamId, string personId)
        => Team(teamId) is null ? null : Members.FirstOrDefault(member => member.TeamId == teamId && member.PersonId == personId)?.Role;

    /// <summary>Finds the owner of a resource that has one of its own.</summary>
    /// <param name="kind">The kind of resource.</param>
    /// <param name="id">Its id.</param>
    /// <returns>The owner record, or null.</returns>
    public ResourceOwnerRecord? Owner(string kind, string id) => Owners.FirstOrDefault(owner => owner.ResourceKind == kind && owner.ResourceId == id);
}

/// <summary>What a change of a role did.</summary>
public enum RoleChangeOutcome
{
    /// <summary>The role changed.</summary>
    Changed,

    /// <summary>No person of the organization has that id.</summary>
    NotFound,

    /// <summary>The change would leave the organization without an active owner.</summary>
    LastOwner,
}

/// <summary>
/// The store of access control (<c>doc/fleet/access-control.md</c>): people's roles and status, teams, service identities,
/// grants, owners of resources, transfers, the chain of the signed access document and the proposals of agents. Every
/// change is written with its audit record in one transaction.
/// </summary>
public interface IAccessStore
{
    /// <summary>Reads everything that decides access in an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The snapshot.</returns>
    ValueTask<AccessSnapshot> ReadAccessAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Adds a person, and the invitation their first device enrolls with.</summary>
    /// <param name="person">The person.</param>
    /// <param name="invitation">The enrollment token of their first device, bound to them.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once written.</returns>
    ValueTask AddPersonAsync(PersonRecord person, EnrollmentTokenRecord invitation, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Changes a person's role; never leaves the organization without an active owner.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="role">The new role.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened.</returns>
    ValueTask<RoleChangeOutcome> SetPersonRoleAsync(string organizationId, string personId, string role, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends a person: their status, their team memberships and grants taken back, and what they owned given to its
    /// successors, paused; never the last active owner.
    /// </summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="now">The time.</param>
    /// <param name="transfers">The transfers made, accepted at once.</param>
    /// <param name="owners">The new owners of what they owned.</param>
    /// <param name="audit">The audit records, the suspension first.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened.</returns>
    ValueTask<RoleChangeOutcome> SuspendPersonAsync(string organizationId, string personId, DateTimeOffset now, IReadOnlyList<TransferRecord> transfers,
        IReadOnlyList<ResourceOwnerRecord> owners, IReadOnlyList<AuditEntry> audit, CancellationToken cancellationToken = default);

    /// <summary>Makes or changes a team, when the version held is the one expected (0 to make it).</summary>
    /// <param name="team">The team, at its new version.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was written.</returns>
    ValueTask<bool> PutTeamAsync(TeamRecord team, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Sets a person's role in a team, or takes them out with a null role; raises the version of the team.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="personId">The person.</param>
    /// <param name="role">Their role, or null.</param>
    /// <param name="now">The time.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether something changed.</returns>
    ValueTask<bool> SetTeamMemberAsync(string teamId, string personId, string? role, DateTimeOffset now, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Makes or changes a service identity.</summary>
    /// <param name="identity">The identity.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once written.</returns>
    ValueTask PutServiceIdentityAsync(ServiceIdentityRecord identity, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Makes a grant.</summary>
    /// <param name="grant">The grant.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once written.</returns>
    ValueTask AddGrantAsync(GrantRecord grant, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Changes where a grant stands, from a status expected.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="expected">The status it must have.</param>
    /// <param name="status">Its new status.</param>
    /// <param name="at">The time.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The grant as written, or null when it was not in the status expected.</returns>
    ValueTask<GrantRecord?> SetGrantStatusAsync(string grantId, string expected, string status, DateTimeOffset at, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Lists which devices hold a copy of the key of each grant of the organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The grants and the devices of their copies.</returns>
    ValueTask<IReadOnlyList<(string GrantId, string DeviceId)>> ListGrantKeyDevicesAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Activates a grant of sealed content, or keys it again: its sealed metadata and the copies of its key replace those held.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="sealedText">The sealed metadata.</param>
    /// <param name="wraps">The copy of the key for each device.</param>
    /// <param name="at">The time.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The grant, active; null when it waits for nothing and is not active.</returns>
    ValueTask<GrantRecord?> ActivateGrantKeysAsync(string grantId, string sealedText, IReadOnlyList<(string DeviceId, string Wrapped)> wraps, DateTimeOffset at, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the keys of active grants given to a device: the grant, its scope and the copy for that device.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The grant, the id of its scope, the sealed metadata and the copy of the key.</returns>
    ValueTask<IReadOnlyList<(string GrantId, string? ScopeId, string Sealed, string Wrapped)>> ListGrantKeysAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Records the owner of a resource, who made it, and whether it is paused.</summary>
    /// <param name="owner">The record.</param>
    /// <param name="audit">The audit record, or null when the change is not one of access (its author recorded at its creation).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once written.</returns>
    ValueTask PutResourceOwnerAsync(ResourceOwnerRecord owner, AuditEntry? audit, CancellationToken cancellationToken = default);

    /// <summary>Offers a transfer; one at a time per resource.</summary>
    /// <param name="transfer">The transfer.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was written: false when another transfer of the resource is offered.</returns>
    ValueTask<bool> OfferTransferAsync(TransferRecord transfer, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Offers a transfer with the hand-over of what the resource does; one at a time per resource.</summary>
    /// <param name="transfer">The transfer.</param>
    /// <param name="sealedText">What the resource does, sealed; null for no hand-over.</param>
    /// <param name="wraps">The copy of its key for each device of who takes it.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was written: false when another transfer of the resource is offered.</returns>
    ValueTask<bool> OfferTransferAsync(TransferRecord transfer, string? sealedText, IReadOnlyList<(string DeviceId, string Wrapped)> wraps, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the hand-over of a transfer for one device.</summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What it does, sealed, and the copy of the key for that device; null for none.</returns>
    ValueTask<(string Sealed, string Wrapped)?> GetTransferHandoverAsync(string transferId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Decides an offered transfer, with the new owner of the resource when it is accepted.</summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="state">The new state.</param>
    /// <param name="personId">Who decides.</param>
    /// <param name="at">The time.</param>
    /// <param name="owner">The new owner record when accepted; null otherwise.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The transfer as written, or null when it was not offered any more.</returns>
    ValueTask<TransferRecord?> DecideTransferAsync(string transferId, string state, string personId, DateTimeOffset at, ResourceOwnerRecord? owner, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a transfer.</summary>
    /// <param name="transferId">The transfer.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The transfer, or null.</returns>
    ValueTask<TransferRecord?> GetTransferAsync(string transferId, CancellationToken cancellationToken = default);

    /// <summary>Lists the transfers of an organization, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="limit">The most listed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The transfers.</returns>
    ValueTask<IReadOnlyList<TransferRecord>> ListTransfersAsync(string organizationId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Lists the versions of the signed access document from a number on.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="afterNumber">The number after which to list; 0 for all.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The versions, in order.</returns>
    ValueTask<IReadOnlyList<AccessDocumentRecord>> ListAccessDocumentsAsync(string organizationId, long afterNumber, CancellationToken cancellationToken = default);

    /// <summary>Appends a version of the signed access document, when it follows the last one.</summary>
    /// <param name="document">The version.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was appended: false when the last one is not the one it follows.</returns>
    ValueTask<bool> AppendAccessDocumentAsync(AccessDocumentRecord document, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Records a proposal of an agent.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once written.</returns>
    ValueTask AddProposalAsync(AccessProposalRecord proposal, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Lists the proposals of an organization, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="limit">The most listed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The proposals.</returns>
    ValueTask<IReadOnlyList<AccessProposalRecord>> ListProposalsAsync(string organizationId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Decides an open proposal.</summary>
    /// <param name="proposalId">The proposal.</param>
    /// <param name="state">The new state.</param>
    /// <param name="personId">Who decides.</param>
    /// <param name="at">The time.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The proposal as written, or null when it was not open.</returns>
    ValueTask<AccessProposalRecord?> DecideProposalAsync(string proposalId, string state, string personId, DateTimeOffset at, AuditEntry audit,
        CancellationToken cancellationToken = default);
}
