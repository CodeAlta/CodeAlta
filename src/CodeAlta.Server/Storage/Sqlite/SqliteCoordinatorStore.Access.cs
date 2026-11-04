using CodeAlta.Fleet.Protocol;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// Access control: teams, service identities, grants, owners of resources, transfers, the signed access document and the
// proposals of agents (the migration "access").
public sealed partial class SqliteCoordinatorStore : IAccessStore
{
    private const string TeamColumns = "SELECT id, organization_id, name, runs_member_automations, created_at, created_by_person_id, dissolved_at, version FROM teams";

    private const string GrantColumns = "SELECT id, organization_id, subject_kind, subject_id, capability, scope_kind, scope_id, status, created_at, created_by_person_id, " +
        "expires_at, revoked_at FROM grants";

    private const string OwnerColumns = "SELECT organization_id, resource_kind, resource_id, owner_kind, owner_id, author_person_id, author_session_id, paused, updated_at " +
        "FROM resource_owners";

    private const string TransferColumns = "SELECT id, organization_id, resource_kind, resource_id, from_kind, from_id, to_kind, to_id, state, reason, offered_by_person_id, " +
        "offered_at, decided_by_person_id, decided_at FROM transfers";

    private const string AccessDocumentColumns = "SELECT organization_id, number, payload, signature, signer_device_id, hash, recorded_at FROM access_documents";

    private const string ProposalColumns = "SELECT id, organization_id, person_id, session_id, kind, summary, payload, state, created_at, decided_by_person_id, decided_at " +
        "FROM access_proposals";

    /// <inheritdoc />
    public ValueTask<AccessSnapshot> ReadAccessAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => new AccessSnapshot(
            QueryList(connection, null, PersonColumns + " FROM people WHERE organization_id = $org ORDER BY created_at, id", ReadPerson, ("$org", organizationId)),
            QueryList(connection, null, TeamColumns + " WHERE organization_id = $org ORDER BY name, id", ReadTeam, ("$org", organizationId)),
            QueryList(connection, null, "SELECT m.team_id, m.person_id, m.role, m.added_at FROM team_members m JOIN teams t ON t.id = m.team_id WHERE t.organization_id = $org " +
                "ORDER BY m.team_id, m.added_at", static reader => new TeamMemberRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), FromTime(reader.GetInt64(3))),
                ("$org", organizationId)),
            QueryList(connection, null, GrantColumns + " WHERE organization_id = $org AND status <> $revoked ORDER BY created_at, id", ReadGrant, ("$org", organizationId),
                ("$revoked", FleetGrantStatuses.Revoked)),
            QueryList(connection, null, "SELECT id, organization_id, name, owner_kind, owner_id, enabled, created_at, created_by_person_id FROM service_identities " +
                "WHERE organization_id = $org ORDER BY name, id", static reader => new ServiceIdentityRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt64(5) != 0, FromTime(reader.GetInt64(6)), reader.GetString(7)), ("$org", organizationId)),
            QueryList(connection, null, OwnerColumns + " WHERE organization_id = $org", ReadOwner, ("$org", organizationId))), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask AddPersonAsync(PersonRecord person, EnrollmentTokenRecord invitation, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(audit);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            InsertPerson(connection, transaction, person);
            Execute(connection, transaction,
                "INSERT INTO enrollment_tokens (id, organization_id, token_hash, label, owner_person_id, created_by_device_id, created_at, expires_at, used_at, used_by_device_id) " +
                "VALUES ($id, $org, $hash, $label, $owner, $by, $created, $expires, NULL, NULL)",
                ("$id", invitation.Id), ("$org", invitation.OrganizationId), ("$hash", invitation.TokenHash), ("$label", invitation.Label), ("$owner", invitation.OwnerPersonId),
                ("$by", invitation.CreatedByDeviceId), ("$created", ToTime(invitation.CreatedAt)), ("$expires", ToTime(invitation.ExpiresAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<RoleChangeOutcome> SetPersonRoleAsync(string organizationId, string personId, string role, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var person = QuerySingle(connection, transaction, PersonColumns + " FROM people WHERE id = $id AND organization_id = $org", ReadPerson,
                ("$id", personId), ("$org", organizationId));
            if (person is null) return RoleChangeOutcome.NotFound;
            if (person.Role == PersonRoles.Owner && role != PersonRoles.Owner && person.IsActive && ActiveOwners(connection, transaction, organizationId) <= 1)
                return RoleChangeOutcome.LastOwner;
            Execute(connection, transaction, "UPDATE people SET role = $role WHERE id = $id", ("$role", role), ("$id", personId));
            AppendAudit(connection, transaction, audit);
            return RoleChangeOutcome.Changed;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<RoleChangeOutcome> SuspendPersonAsync(string organizationId, string personId, DateTimeOffset now, IReadOnlyList<TransferRecord> transfers,
        IReadOnlyList<ResourceOwnerRecord> owners, IReadOnlyList<AuditEntry> audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentNullException.ThrowIfNull(transfers);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var person = QuerySingle(connection, transaction, PersonColumns + " FROM people WHERE id = $id AND organization_id = $org", ReadPerson,
                ("$id", personId), ("$org", organizationId));
            if (person is null) return RoleChangeOutcome.NotFound;
            if (person.Role == PersonRoles.Owner && person.IsActive && ActiveOwners(connection, transaction, organizationId) <= 1) return RoleChangeOutcome.LastOwner;
            Execute(connection, transaction, "UPDATE people SET status = $status, suspended_at = $at WHERE id = $id",
                ("$status", FleetPersonStatuses.Suspended), ("$at", ToTime(now)), ("$id", personId));
            // Their teams raise their versions: the next signed document says they left.
            Execute(connection, transaction, "UPDATE teams SET version = version + 1 WHERE id IN (SELECT team_id FROM team_members WHERE person_id = $id)", ("$id", personId));
            Execute(connection, transaction, "DELETE FROM team_members WHERE person_id = $id", ("$id", personId));
            Execute(connection, transaction, "DELETE FROM grant_keys WHERE grant_id IN (SELECT id FROM grants WHERE organization_id = $org AND subject_kind = $person " +
                "AND subject_id = $id) OR device_id IN (SELECT id FROM devices WHERE owner_person_id = $id)", ("$org", organizationId), ("$person", FleetPrincipalKinds.Person),
                ("$id", personId));
            Execute(connection, transaction, "UPDATE grants SET status = $revoked, revoked_at = $at WHERE organization_id = $org AND subject_kind = $person AND subject_id = $id " +
                "AND status <> $revoked", ("$revoked", FleetGrantStatuses.Revoked), ("$at", ToTime(now)), ("$org", organizationId), ("$person", FleetPrincipalKinds.Person),
                ("$id", personId));
            // An offer they made or were made ends with them.
            Execute(connection, transaction, "UPDATE transfers SET state = $cancelled, decided_at = $at WHERE organization_id = $org AND state = $offered " +
                "AND ((from_kind = $person AND from_id = $id) OR (to_kind = $person AND to_id = $id))", ("$cancelled", FleetTransferStates.Cancelled), ("$at", ToTime(now)),
                ("$org", organizationId), ("$offered", FleetTransferStates.Offered), ("$person", FleetPrincipalKinds.Person), ("$id", personId));
            foreach (var transfer in transfers) InsertTransfer(connection, transaction, transfer);
            foreach (var owner in owners) UpsertOwner(connection, transaction, owner);
            foreach (var entry in audit) AppendAudit(connection, transaction, entry);
            return RoleChangeOutcome.Changed;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> PutTeamAsync(TeamRecord team, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var held = QuerySingle(connection, transaction, TeamColumns + " WHERE id = $id", ReadTeam, ("$id", team.Id));
            if (held is null ? team.Version != 1 : held.OrganizationId != team.OrganizationId || held.Version != team.Version - 1) return false;
            Execute(connection, transaction,
                "INSERT INTO teams (id, organization_id, name, runs_member_automations, created_at, created_by_person_id, dissolved_at, version) " +
                "VALUES ($id, $org, $name, $runs, $created, $by, $dissolved, $version) ON CONFLICT (id) DO UPDATE SET name = excluded.name, " +
                "runs_member_automations = excluded.runs_member_automations, dissolved_at = excluded.dissolved_at, version = excluded.version",
                ("$id", team.Id), ("$org", team.OrganizationId), ("$name", team.Name), ("$runs", team.RunsMemberAutomations ? 1L : 0L), ("$created", ToTime(team.CreatedAt)),
                ("$by", team.CreatedByPersonId), ("$dissolved", ToNullableTime(team.DissolvedAt)), ("$version", team.Version));
            if (team.DissolvedAt is not null) Execute(connection, transaction, "DELETE FROM team_members WHERE team_id = $id", ("$id", team.Id));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> SetTeamMemberAsync(string teamId, string personId, string? role, DateTimeOffset now, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var held = QuerySingle(connection, transaction, "SELECT role FROM team_members WHERE team_id = $team AND person_id = $person", static reader => reader.GetString(0),
                ("$team", teamId), ("$person", personId));
            if (held == role) return false;
            if (role is null)
            {
                Execute(connection, transaction, "DELETE FROM team_members WHERE team_id = $team AND person_id = $person", ("$team", teamId), ("$person", personId));
            }
            else
            {
                Execute(connection, transaction, "INSERT INTO team_members (team_id, person_id, role, added_at) VALUES ($team, $person, $role, $at) " +
                    "ON CONFLICT (team_id, person_id) DO UPDATE SET role = excluded.role", ("$team", teamId), ("$person", personId), ("$role", role), ("$at", ToTime(now)));
            }

            Execute(connection, transaction, "UPDATE teams SET version = version + 1 WHERE id = $team", ("$team", teamId));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PutServiceIdentityAsync(ServiceIdentityRecord identity, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(audit);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction,
                "INSERT INTO service_identities (id, organization_id, name, owner_kind, owner_id, enabled, created_at, created_by_person_id) " +
                "VALUES ($id, $org, $name, $kind, $owner, $enabled, $created, $by) ON CONFLICT (id) DO UPDATE SET name = excluded.name, enabled = excluded.enabled",
                ("$id", identity.Id), ("$org", identity.OrganizationId), ("$name", identity.Name), ("$kind", identity.OwnerKind), ("$owner", identity.OwnerId),
                ("$enabled", identity.Enabled ? 1L : 0L), ("$created", ToTime(identity.CreatedAt)), ("$by", identity.CreatedByPersonId));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask AddGrantAsync(GrantRecord grant, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(audit);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction,
                "INSERT INTO grants (id, organization_id, subject_kind, subject_id, capability, scope_kind, scope_id, status, created_at, created_by_person_id, expires_at, revoked_at) " +
                "VALUES ($id, $org, $sk, $sid, $cap, $scope, $scopeId, $status, $created, $by, $expires, NULL)",
                ("$id", grant.Id), ("$org", grant.OrganizationId), ("$sk", grant.SubjectKind), ("$sid", grant.SubjectId), ("$cap", grant.Capability),
                ("$scope", grant.ScopeKind), ("$scopeId", grant.ScopeId), ("$status", grant.Status), ("$created", ToTime(grant.CreatedAt)), ("$by", grant.CreatedByPersonId),
                ("$expires", ToNullableTime(grant.ExpiresAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<GrantRecord?> SetGrantStatusAsync(string grantId, string expected, string status, DateTimeOffset at, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var changed = Execute(connection, transaction, "UPDATE grants SET status = $status, revoked_at = $revokedAt WHERE id = $id AND status = $expected",
                ("$status", status), ("$revokedAt", status == FleetGrantStatuses.Revoked ? ToTime(at) : null), ("$id", grantId), ("$expected", expected));
            if (changed == 0) return null;
            if (status == FleetGrantStatuses.Revoked) Execute(connection, transaction, "DELETE FROM grant_keys WHERE grant_id = $id", ("$id", grantId));
            AppendAudit(connection, transaction, audit);
            return QuerySingle(connection, transaction, GrantColumns + " WHERE id = $id", ReadGrant, ("$id", grantId));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<GrantRecord?> ActivateGrantKeysAsync(string grantId, string sealedText, IReadOnlyList<(string DeviceId, string Wrapped)> wraps, DateTimeOffset at,
        AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        ArgumentNullException.ThrowIfNull(sealedText);
        ArgumentNullException.ThrowIfNull(wraps);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var changed = Execute(connection, transaction, "UPDATE grants SET status = $active WHERE id = $id AND status = $pending",
                ("$active", FleetGrantStatuses.Active), ("$id", grantId), ("$pending", FleetGrantStatuses.PendingKeys));
            if (changed == 0) return null;
            foreach (var (deviceId, wrapped) in wraps)
            {
                Execute(connection, transaction, "INSERT INTO grant_keys (grant_id, device_id, sealed, wrapped, created_at) VALUES ($grant, $device, $sealed, $wrapped, $at) " +
                    "ON CONFLICT (grant_id, device_id) DO UPDATE SET sealed = excluded.sealed, wrapped = excluded.wrapped, created_at = excluded.created_at",
                    ("$grant", grantId), ("$device", deviceId), ("$sealed", sealedText), ("$wrapped", wrapped), ("$at", ToTime(at)));
            }

            AppendAudit(connection, transaction, audit);
            return QuerySingle(connection, transaction, GrantColumns + " WHERE id = $id", ReadGrant, ("$id", grantId));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<(string GrantId, string? ScopeId, string Sealed, string Wrapped)>> ListGrantKeysAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return ReadAsync(connection => QueryList(connection, null,
            "SELECT k.grant_id, g.scope_id, k.sealed, k.wrapped FROM grant_keys k JOIN grants g ON g.id = k.grant_id WHERE k.device_id = $device AND g.status = $active",
            static reader => (reader.GetString(0), GetNullableString(reader, 1), reader.GetString(2), reader.GetString(3)), ("$device", deviceId),
            ("$active", FleetGrantStatuses.Active)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PutResourceOwnerAsync(ResourceOwnerRecord owner, AuditEntry? audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            UpsertOwner(connection, transaction, owner);
            if (audit is not null) AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<bool> OfferTransferAsync(TransferRecord transfer, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var open = QuerySingle(connection, transaction, "SELECT id FROM transfers WHERE organization_id = $org AND resource_kind = $kind AND resource_id = $id AND state = $offered",
                static reader => reader.GetString(0), ("$org", transfer.OrganizationId), ("$kind", transfer.ResourceKind), ("$id", transfer.ResourceId),
                ("$offered", FleetTransferStates.Offered));
            if (open is not null) return false;
            InsertTransfer(connection, transaction, transfer);
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TransferRecord?> DecideTransferAsync(string transferId, string state, string personId, DateTimeOffset at, ResourceOwnerRecord? owner, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var changed = Execute(connection, transaction,
                "UPDATE transfers SET state = $state, decided_by_person_id = $by, decided_at = $at WHERE id = $id AND state = $offered",
                ("$state", state), ("$by", personId), ("$at", ToTime(at)), ("$id", transferId), ("$offered", FleetTransferStates.Offered));
            if (changed == 0) return null;
            if (owner is not null) UpsertOwner(connection, transaction, owner);
            AppendAudit(connection, transaction, audit);
            return QuerySingle(connection, transaction, TransferColumns + " WHERE id = $id", ReadTransfer, ("$id", transferId));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TransferRecord?> GetTransferAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        return ReadAsync(connection => QuerySingle(connection, null, TransferColumns + " WHERE id = $id", ReadTransfer, ("$id", transferId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TransferRecord>> ListTransfersAsync(string organizationId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, TransferColumns + " WHERE organization_id = $org ORDER BY offered_at DESC, id LIMIT $limit", ReadTransfer,
            ("$org", organizationId), ("$limit", (long)Math.Max(1, limit))), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AccessDocumentRecord>> ListAccessDocumentsAsync(string organizationId, long afterNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, AccessDocumentColumns + " WHERE organization_id = $org AND number > $after ORDER BY number",
            ReadAccessDocument, ("$org", organizationId), ("$after", Math.Max(0, afterNumber))), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> AppendAccessDocumentAsync(AccessDocumentRecord document, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var last = QuerySingle(connection, transaction, "SELECT MAX(number) FROM access_documents WHERE organization_id = $org",
                static reader => reader.IsDBNull(0) ? 0L : reader.GetInt64(0), ("$org", document.OrganizationId));
            if (last != document.Number - 1) return false;
            Execute(connection, transaction,
                "INSERT INTO access_documents (organization_id, number, payload, signature, signer_device_id, hash, recorded_at) " +
                "VALUES ($org, $number, $payload, $signature, $signer, $hash, $at)",
                ("$org", document.OrganizationId), ("$number", document.Number), ("$payload", document.Payload), ("$signature", document.Signature),
                ("$signer", document.SignerDeviceId), ("$hash", document.Hash), ("$at", ToTime(document.RecordedAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask AddProposalAsync(AccessProposalRecord proposal, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(audit);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction,
                "INSERT INTO access_proposals (id, organization_id, person_id, session_id, kind, summary, payload, state, created_at, decided_by_person_id, decided_at) " +
                "VALUES ($id, $org, $person, $session, $kind, $summary, $payload, $state, $created, NULL, NULL)",
                ("$id", proposal.Id), ("$org", proposal.OrganizationId), ("$person", proposal.PersonId), ("$session", proposal.SessionId), ("$kind", proposal.Kind),
                ("$summary", proposal.Summary), ("$payload", proposal.Payload), ("$state", proposal.State), ("$created", ToTime(proposal.CreatedAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AccessProposalRecord>> ListProposalsAsync(string organizationId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, ProposalColumns + " WHERE organization_id = $org ORDER BY created_at DESC, id LIMIT $limit", ReadProposal,
            ("$org", organizationId), ("$limit", (long)Math.Max(1, limit))), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AccessProposalRecord?> DecideProposalAsync(string proposalId, string state, string personId, DateTimeOffset at, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(proposalId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var changed = Execute(connection, transaction,
                "UPDATE access_proposals SET state = $state, decided_by_person_id = $by, decided_at = $at WHERE id = $id AND state = $open",
                ("$state", state), ("$by", personId), ("$at", ToTime(at)), ("$id", proposalId), ("$open", Fleet.Protocol.Api.FleetAccessProposalStates.Open));
            if (changed == 0) return null;
            AppendAudit(connection, transaction, audit);
            return QuerySingle(connection, transaction, ProposalColumns + " WHERE id = $id", ReadProposal, ("$id", proposalId));
        }, cancellationToken);
    }

    private static long ActiveOwners(SqliteConnection connection, SqliteTransaction transaction, string organizationId)
        => QuerySingle(connection, transaction, "SELECT COUNT(*) FROM people WHERE organization_id = $org AND role = $owner AND status = $active",
            static reader => reader.GetInt64(0), ("$org", organizationId), ("$owner", PersonRoles.Owner), ("$active", FleetPersonStatuses.Active));

    private static void InsertTransfer(SqliteConnection connection, SqliteTransaction transaction, TransferRecord transfer)
        => Execute(connection, transaction,
            "INSERT INTO transfers (id, organization_id, resource_kind, resource_id, from_kind, from_id, to_kind, to_id, state, reason, offered_by_person_id, offered_at, " +
            "decided_by_person_id, decided_at) VALUES ($id, $org, $rk, $rid, $fk, $fid, $tk, $tid, $state, $reason, $by, $at, $decidedBy, $decidedAt)",
            ("$id", transfer.Id), ("$org", transfer.OrganizationId), ("$rk", transfer.ResourceKind), ("$rid", transfer.ResourceId), ("$fk", transfer.FromKind),
            ("$fid", transfer.FromId), ("$tk", transfer.ToKind), ("$tid", transfer.ToId), ("$state", transfer.State), ("$reason", transfer.Reason),
            ("$by", transfer.OfferedByPersonId), ("$at", ToTime(transfer.OfferedAt)), ("$decidedBy", transfer.DecidedByPersonId), ("$decidedAt", ToNullableTime(transfer.DecidedAt)));

    private static void UpsertOwner(SqliteConnection connection, SqliteTransaction transaction, ResourceOwnerRecord owner)
        => Execute(connection, transaction,
            "INSERT INTO resource_owners (organization_id, resource_kind, resource_id, owner_kind, owner_id, author_person_id, author_session_id, paused, updated_at) " +
            "VALUES ($org, $kind, $id, $ok, $oid, $author, $session, $paused, $at) ON CONFLICT (organization_id, resource_kind, resource_id) DO UPDATE SET " +
            "owner_kind = excluded.owner_kind, owner_id = excluded.owner_id, author_person_id = COALESCE(resource_owners.author_person_id, excluded.author_person_id), " +
            "author_session_id = COALESCE(resource_owners.author_session_id, excluded.author_session_id), paused = excluded.paused, updated_at = excluded.updated_at",
            ("$org", owner.OrganizationId), ("$kind", owner.ResourceKind), ("$id", owner.ResourceId), ("$ok", owner.OwnerKind), ("$oid", owner.OwnerId),
            ("$author", owner.AuthorPersonId), ("$session", owner.AuthorSessionId), ("$paused", owner.Paused ? 1L : 0L), ("$at", ToTime(owner.UpdatedAt)));

    private static TeamRecord ReadTeam(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, FromTime(reader.GetInt64(4)), reader.GetString(5),
            GetNullableTime(reader, 6), reader.GetInt64(7));

    private static GrantRecord ReadGrant(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), GetNullableString(reader, 6),
            reader.GetString(7), FromTime(reader.GetInt64(8)), reader.GetString(9), GetNullableTime(reader, 10), GetNullableTime(reader, 11));

    private static ResourceOwnerRecord ReadOwner(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), GetNullableString(reader, 5),
            GetNullableString(reader, 6), reader.GetInt64(7) != 0, FromTime(reader.GetInt64(8)));

    private static TransferRecord ReadTransfer(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), FromTime(reader.GetInt64(11)), GetNullableString(reader, 12),
            GetNullableTime(reader, 13));

    private static AccessDocumentRecord ReadAccessDocument(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), FromTime(reader.GetInt64(6)));

    private static AccessProposalRecord ReadProposal(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), FromTime(reader.GetInt64(8)), GetNullableString(reader, 9), GetNullableTime(reader, 10));
}
