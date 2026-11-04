using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// The secrets, variables, environments, approvals and uses of the organization (migration 0011).
public sealed partial class SqliteCoordinatorStore : IFleetSecretStore
{
    private const string SecretColumns = "SELECT id, organization_id, owner_person_id, kind, name, scope_kind, scope_id, host_only, version, set_by_device_id, set_by_person_id, " +
        "set_at, created_at FROM fleet_secrets";

    private const string CopyColumns = "SELECT secret_id, device_id, version, sealed, sealed_by_device_id, created_at FROM fleet_secret_copies";

    private const string EnvironmentColumns = "SELECT id, organization_id, owner_person_id, name, number, removed, payload, signature, signer_device_id, updated_at FROM fleet_environments";

    private const string ApprovalColumns = "SELECT id, organization_id, environment_id, environment_number, machine_id, run_id, nonce, automation_id, title, status, requested_at, " +
        "approval_payload, approval_signature, approver_device_id, approved_at FROM fleet_approvals";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FleetSecretEntry>> ListFleetSecretsAsync(string organizationId, string ownerPersonId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(ownerPersonId);
        return ReadAsync(connection =>
        {
            var secrets = QueryList(connection, null, SecretColumns + " WHERE organization_id = $org AND owner_person_id = $owner ORDER BY name_key, scope_kind, scope_id",
                ReadSecret, ("$org", organizationId), ("$owner", ownerPersonId));
            var copies = QueryList(connection, null,
                    "SELECT c.secret_id, c.device_id, c.version, c.sealed, c.sealed_by_device_id, c.created_at FROM fleet_secret_copies c " +
                    "JOIN fleet_secrets s ON s.id = c.secret_id AND s.version = c.version WHERE s.organization_id = $org AND s.owner_person_id = $owner",
                    ReadCopy, ("$org", organizationId), ("$owner", ownerPersonId))
                .ToLookup(static copy => copy.SecretId, StringComparer.Ordinal);
            return (IReadOnlyList<FleetSecretEntry>)[.. secrets.Select(secret => new FleetSecretEntry(secret, [.. copies[secret.Id]]))];
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FleetSecretEntry?> GetFleetSecretAsync(string organizationId, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ReadAsync(connection => ReadEntry(connection, null, organizationId, id), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FleetSecretEntry>> ListHeldFleetSecretsAsync(string organizationId, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return ReadAsync(connection =>
        {
            var copies = QueryList(connection, null,
                "SELECT c.secret_id, c.device_id, c.version, c.sealed, c.sealed_by_device_id, c.created_at FROM fleet_secret_copies c " +
                "JOIN fleet_secrets s ON s.id = c.secret_id AND s.version = c.version WHERE s.organization_id = $org AND c.device_id = $device",
                ReadCopy, ("$org", organizationId), ("$device", deviceId));
            var held = new List<FleetSecretEntry>(copies.Count);
            foreach (var copy in copies)
            {
                if (QuerySingle(connection, null, SecretColumns + " WHERE id = $id", ReadSecret, ("$id", copy.SecretId)) is { } secret) held.Add(new(secret, [copy]));
            }

            return (IReadOnlyList<FleetSecretEntry>)held;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FleetSecretPutOutcome> PutFleetSecretAsync(FleetSecretRecord secret, IReadOnlyList<FleetSecretCopyRecord> copies, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var held = QuerySingle(connection, transaction, SecretColumns + " WHERE id = $id", ReadSecret, ("$id", secret.Id));
            if (secret.Version == 1)
            {
                if (held is not null) return FleetSecretPutOutcome.Stale;
                if (CountNamed(connection, transaction, secret) > 0) return FleetSecretPutOutcome.NameTaken;
                Execute(connection, transaction,
                    "INSERT INTO fleet_secrets (id, organization_id, owner_person_id, kind, name, name_key, scope_kind, scope_id, host_only, version, set_by_device_id, " +
                    "set_by_person_id, set_at, created_at) VALUES ($id, $org, $owner, $kind, $name, $key, $scope, $scopeId, $hostOnly, 1, $by, $person, $at, $at)",
                    ("$id", secret.Id), ("$org", secret.OrganizationId), ("$owner", secret.OwnerPersonId), ("$kind", secret.Kind), ("$name", secret.Name),
                    ("$key", NameKey(secret.Name)), ("$scope", secret.ScopeKind), ("$scopeId", secret.ScopeId), ("$hostOnly", secret.HostOnly), ("$by", secret.SetByDeviceId),
                    ("$person", secret.SetByPersonId), ("$at", ToTime(secret.SetAt)));
            }
            else
            {
                // A rotation follows the version before it, of the same value of the same person: a replay changes nothing.
                if (held is null || held.Version != secret.Version - 1 || held.OrganizationId != secret.OrganizationId || held.OwnerPersonId != secret.OwnerPersonId
                    || held.Kind != secret.Kind || held.ScopeKind != secret.ScopeKind || held.ScopeId != secret.ScopeId || NameKey(held.Name) != NameKey(secret.Name))
                    return FleetSecretPutOutcome.Stale;
                Execute(connection, transaction,
                    "UPDATE fleet_secrets SET name = $name, host_only = $hostOnly, version = $version, set_by_device_id = $by, set_by_person_id = $person, set_at = $at " +
                    "WHERE id = $id AND version = $previous",
                    ("$id", secret.Id), ("$name", secret.Name), ("$hostOnly", secret.HostOnly), ("$version", secret.Version), ("$previous", secret.Version - 1),
                    ("$by", secret.SetByDeviceId), ("$person", secret.SetByPersonId), ("$at", ToTime(secret.SetAt)));
                // The copies of the old version go: a machine takes the new one at its next run.
                Execute(connection, transaction, "DELETE FROM fleet_secret_copies WHERE secret_id = $id", ("$id", secret.Id));
            }

            foreach (var copy in copies) InsertCopy(connection, transaction, copy);
            AppendAudit(connection, transaction, audit);
            return FleetSecretPutOutcome.Written;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>?> AddFleetSecretCopiesAsync(string organizationId, string id, int version, IReadOnlyList<FleetSecretCopyRecord> copies,
        AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync<IReadOnlyList<string>?>((connection, transaction) =>
        {
            var held = QuerySingle(connection, transaction, SecretColumns + " WHERE id = $id AND organization_id = $org", ReadSecret, ("$id", id), ("$org", organizationId));
            if (held is null || held.Version != version) return null;
            var added = new List<string>();
            foreach (var copy in copies)
            {
                if (InsertCopy(connection, transaction, copy) > 0) added.Add(copy.DeviceId);
            }

            if (added.Count > 0) AppendAudit(connection, transaction, audit);
            return added;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveFleetSecretCopyAsync(string organizationId, string id, string deviceId, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (Execute(connection, transaction,
                    "DELETE FROM fleet_secret_copies WHERE secret_id = $id AND device_id = $device AND EXISTS (SELECT 1 FROM fleet_secrets WHERE id = $id AND organization_id = $org)",
                    ("$id", id), ("$device", deviceId), ("$org", organizationId)) == 0)
                return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteFleetSecretAsync(string organizationId, string id, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction, "DELETE FROM fleet_secret_copies WHERE secret_id = $id AND EXISTS (SELECT 1 FROM fleet_secrets WHERE id = $id AND organization_id = $org)",
                ("$id", id), ("$org", organizationId));
            if (Execute(connection, transaction, "DELETE FROM fleet_secrets WHERE id = $id AND organization_id = $org", ("$id", id), ("$org", organizationId)) == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> PutFleetEnvironmentAsync(FleetEnvironmentRecord environment, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var named = QuerySingle(connection, transaction, EnvironmentColumns + " WHERE owner_person_id = $owner AND name = $name", ReadEnvironment,
                ("$owner", environment.OwnerPersonId), ("$name", environment.Name));
            if (named is not null && named.Id != environment.Id) return false;
            var changed = Execute(connection, transaction,
                "INSERT INTO fleet_environments (id, organization_id, owner_person_id, name, number, removed, payload, signature, signer_device_id, updated_at) " +
                "VALUES ($id, $org, $owner, $name, $number, $removed, $payload, $signature, $signer, $at) " +
                "ON CONFLICT (id) DO UPDATE SET name = excluded.name, number = excluded.number, removed = excluded.removed, payload = excluded.payload, " +
                "signature = excluded.signature, signer_device_id = excluded.signer_device_id, updated_at = excluded.updated_at " +
                "WHERE fleet_environments.number < excluded.number AND fleet_environments.owner_person_id = excluded.owner_person_id " +
                "AND fleet_environments.organization_id = excluded.organization_id",
                ("$id", environment.Id), ("$org", environment.OrganizationId), ("$owner", environment.OwnerPersonId), ("$name", environment.Name),
                ("$number", environment.Number), ("$removed", environment.Removed), ("$payload", environment.Payload), ("$signature", environment.Signature),
                ("$signer", environment.SignerDeviceId), ("$at", ToTime(environment.UpdatedAt)));
            if (changed == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FleetEnvironmentRecord>> ListFleetEnvironmentsAsync(string organizationId, string? ownerPersonId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => ownerPersonId is null
            ? QueryList(connection, null, EnvironmentColumns + " WHERE organization_id = $org ORDER BY name", ReadEnvironment, ("$org", organizationId))
            : QueryList(connection, null, EnvironmentColumns + " WHERE organization_id = $org AND owner_person_id = $owner ORDER BY name", ReadEnvironment,
                ("$org", organizationId), ("$owner", ownerPersonId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FleetApprovalRecord> RequestFleetApprovalAsync(FleetApprovalRecord approval, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (QuerySingle(connection, transaction, ApprovalColumns + " WHERE machine_id = $machine AND run_id = $run", ReadApproval,
                    ("$machine", approval.MachineId), ("$run", approval.RunId)) is { } held)
                return held;
            Execute(connection, transaction,
                "INSERT INTO fleet_approvals (id, organization_id, environment_id, environment_number, machine_id, run_id, nonce, automation_id, title, status, requested_at) " +
                "VALUES ($id, $org, $environment, $number, $machine, $run, $nonce, $automation, $title, $status, $at)",
                ("$id", approval.Id), ("$org", approval.OrganizationId), ("$environment", approval.EnvironmentId), ("$number", approval.EnvironmentNumber),
                ("$machine", approval.MachineId), ("$run", approval.RunId), ("$nonce", approval.Nonce), ("$automation", approval.AutomationId), ("$title", approval.Title),
                ("$status", approval.Status), ("$at", ToTime(approval.RequestedAt)));
            AppendAudit(connection, transaction, audit);
            return approval;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FleetApprovalRecord?> GetFleetApprovalAsync(string organizationId, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ReadAsync(connection => QuerySingle(connection, null, ApprovalColumns + " WHERE organization_id = $org AND id = $id", ReadApproval,
            ("$org", organizationId), ("$id", id)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FleetApprovalRecord>> ListFleetApprovalsAsync(string organizationId, IReadOnlyCollection<string> environmentIds, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentNullException.ThrowIfNull(environmentIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync(connection =>
        {
            var all = new List<FleetApprovalRecord>();
            foreach (var environment in environmentIds)
            {
                all.AddRange(QueryList(connection, null, ApprovalColumns + " WHERE organization_id = $org AND environment_id = $environment ORDER BY requested_at DESC LIMIT $limit",
                    ReadApproval, ("$org", organizationId), ("$environment", environment), ("$limit", limit)));
            }

            return (IReadOnlyList<FleetApprovalRecord>)[.. all.OrderByDescending(static approval => approval.RequestedAt).Take(limit)];
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> ApproveFleetApprovalAsync(string organizationId, string id, string payload, string signature, string approverDeviceId, DateTimeOffset at,
        AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);
        ArgumentException.ThrowIfNullOrEmpty(approverDeviceId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (Execute(connection, transaction,
                    "UPDATE fleet_approvals SET status = 'approved', approval_payload = $payload, approval_signature = $signature, approver_device_id = $device, approved_at = $at " +
                    "WHERE organization_id = $org AND id = $id AND status = 'pending'",
                    ("$org", organizationId), ("$id", id), ("$payload", payload), ("$signature", signature), ("$device", approverDeviceId), ("$at", ToTime(at))) == 0)
                return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<int> RecordFleetSecretUsesAsync(IReadOnlyList<FleetSecretUseRecord> uses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uses);
        return WriteAsync((connection, transaction) =>
        {
            var added = 0;
            foreach (var use in uses)
            {
                added += Execute(connection, transaction,
                    "INSERT INTO fleet_secret_uses (organization_id, secret_id, machine_id, name, version, run_id, used_at, received_at) " +
                    "VALUES ($org, $secret, $machine, $name, $version, $run, $used, $received) ON CONFLICT (secret_id, machine_id, run_id, version) DO NOTHING",
                    ("$org", use.OrganizationId), ("$secret", use.SecretId), ("$machine", use.MachineId), ("$name", use.Name), ("$version", use.Version),
                    ("$run", use.RunId), ("$used", ToTime(use.UsedAt)), ("$received", ToTime(use.ReceivedAt)));
            }

            return added;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FleetSecretUseRecord>> ListFleetSecretUsesAsync(string organizationId, string secretId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(secretId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync(connection => QueryList(connection, null,
            "SELECT organization_id, secret_id, machine_id, name, version, run_id, used_at, received_at FROM fleet_secret_uses " +
            "WHERE organization_id = $org AND secret_id = $secret ORDER BY used_at DESC, seq DESC LIMIT $limit",
            static reader => new FleetSecretUseRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
                FromTime(reader.GetInt64(6)), FromTime(reader.GetInt64(7))),
            ("$org", organizationId), ("$secret", secretId), ("$limit", limit)), cancellationToken);
    }

    private static FleetSecretEntry? ReadEntry(SqliteConnection connection, SqliteTransaction? transaction, string organizationId, string id)
    {
        if (QuerySingle(connection, transaction, SecretColumns + " WHERE organization_id = $org AND id = $id", ReadSecret, ("$org", organizationId), ("$id", id)) is not { } secret)
            return null;
        return new FleetSecretEntry(secret, QueryList(connection, transaction, CopyColumns + " WHERE secret_id = $id AND version = $version ORDER BY device_id", ReadCopy,
            ("$id", id), ("$version", secret.Version)));
    }

    // Another value of the person has the name of this one in its scope.
    private static long CountNamed(SqliteConnection connection, SqliteTransaction transaction, FleetSecretRecord secret)
    {
        using var command = Create(connection, transaction,
            "SELECT COUNT(*) FROM fleet_secrets WHERE owner_person_id = $owner AND kind = $kind AND scope_kind = $scope AND scope_id = $scopeId AND name_key = $key",
            ("$owner", secret.OwnerPersonId), ("$kind", secret.Kind), ("$scope", secret.ScopeKind), ("$scopeId", secret.ScopeId), ("$key", NameKey(secret.Name)));
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // A device keeps the copy it has of a version: one per device.
    private static int InsertCopy(SqliteConnection connection, SqliteTransaction transaction, FleetSecretCopyRecord copy)
        => Execute(connection, transaction,
            "INSERT INTO fleet_secret_copies (secret_id, device_id, version, sealed, sealed_by_device_id, created_at) VALUES ($secret, $device, $version, $sealed, $by, $at) " +
            "ON CONFLICT (secret_id, device_id) DO NOTHING",
            ("$secret", copy.SecretId), ("$device", copy.DeviceId), ("$version", copy.Version), ("$sealed", copy.Sealed), ("$by", copy.SealedByDeviceId),
            ("$at", ToTime(copy.CreatedAt)));

    private static string NameKey(string name) => name.ToUpperInvariant();

    private static FleetSecretRecord ReadSecret(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetInt64(7) != 0, reader.GetInt32(8), reader.GetString(9), GetNullableString(reader, 10), FromTime(reader.GetInt64(11)), FromTime(reader.GetInt64(12)));

    private static FleetSecretCopyRecord ReadCopy(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), FromTime(reader.GetInt64(5)));

    private static FleetEnvironmentRecord ReadEnvironment(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5) != 0, reader.GetString(6),
            reader.GetString(7), reader.GetString(8), FromTime(reader.GetInt64(9)));

    private static FleetApprovalRecord ReadApproval(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            GetNullableString(reader, 7), GetNullableString(reader, 8), reader.GetString(9), FromTime(reader.GetInt64(10)), GetNullableString(reader, 11),
            GetNullableString(reader, 12), GetNullableString(reader, 13), GetNullableTime(reader, 14));
}
