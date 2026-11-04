using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// The on-call rotations of teams (migration 0016).
public sealed partial class SqliteCoordinatorStore : IOnCallStore
{
    private const string OnCallColumns = "SELECT organization_id, team_id, people, starts_at, shift_hours, updated_at, updated_by_person_id FROM oncall_rotations";

    /// <inheritdoc />
    public ValueTask<OnCallRotationRecord?> GetOnCallRotationAsync(string organizationId, string teamId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        return ReadAsync(connection => QuerySingle(connection, null, OnCallColumns + " WHERE organization_id = $org AND team_id = $team", ReadOnCall,
            ("$org", organizationId), ("$team", teamId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OnCallRotationRecord>> ListOnCallRotationsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, OnCallColumns + " WHERE organization_id = $org ORDER BY team_id", ReadOnCall, ("$org", organizationId)),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PutOnCallRotationAsync(OnCallRotationRecord rotation, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(audit);
        return new(WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction,
                "INSERT INTO oncall_rotations (organization_id, team_id, people, starts_at, shift_hours, updated_at, updated_by_person_id) " +
                "VALUES ($org, $team, $people, $starts, $shift, $at, $by) ON CONFLICT (organization_id, team_id) DO UPDATE SET people = excluded.people, " +
                "starts_at = excluded.starts_at, shift_hours = excluded.shift_hours, updated_at = excluded.updated_at, updated_by_person_id = excluded.updated_by_person_id",
                ("$org", rotation.OrganizationId), ("$team", rotation.TeamId), ("$people", JsonSerializer.Serialize([.. rotation.People], OnCallStoreJsonContext.Default.StringArray)),
                ("$starts", ToTime(rotation.StartsAt)), ("$shift", rotation.ShiftHours), ("$at", ToTime(rotation.UpdatedAt)), ("$by", rotation.UpdatedByPersonId));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteOnCallRotationAsync(string organizationId, string teamId, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (Execute(connection, transaction, "DELETE FROM oncall_rotations WHERE organization_id = $org AND team_id = $team", ("$org", organizationId), ("$team", teamId)) == 0)
                return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    private static OnCallRotationRecord ReadOnCall(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), JsonSerializer.Deserialize(reader.GetString(2), OnCallStoreJsonContext.Default.StringArray) ?? [],
            FromTime(reader.GetInt64(3)), reader.GetInt32(4), FromTime(reader.GetInt64(5)), reader.GetString(6));
}

/// <summary>The people of a rotation as the store keeps them.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class OnCallStoreJsonContext : JsonSerializerContext;
