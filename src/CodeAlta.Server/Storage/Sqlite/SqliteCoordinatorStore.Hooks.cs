using CodeAlta.Fleet.Protocol.Api;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// The hooks of the server and the events they received (migration 0015).
public sealed partial class SqliteCoordinatorStore : IHookStore
{
    private const string HookColumns = "SELECT organization_id, hook_id, owner_person_id, machine_id, source, label, created_by_device_id, created_at, last_received_at FROM hooks";

    private const string HookEventColumns = "SELECT organization_id, delivery_id, hook_id, machine_id, event_key, received_at, expires_at, state, message, answered_at";

    /// <inheritdoc />
    public ValueTask CreateHookAsync(HookRecord hook, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(audit);
        return new(WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction,
                "INSERT INTO hooks (organization_id, hook_id, owner_person_id, machine_id, source, label, created_by_device_id, created_at, last_received_at) " +
                "VALUES ($org, $id, $owner, $machine, $source, $label, $device, $at, NULL)",
                ("$org", hook.OrganizationId), ("$id", hook.HookId), ("$owner", hook.OwnerPersonId), ("$machine", hook.MachineId), ("$source", hook.Source),
                ("$label", hook.Label), ("$device", hook.CreatedByDeviceId), ("$at", ToTime(hook.CreatedAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<HookRecord?> GetHookAsync(string hookId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(hookId);
        return ReadAsync(connection => QuerySingle(connection, null, HookColumns + " WHERE hook_id = $id", ReadHook, ("$id", hookId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HookRecord>> ListHooksAsync(string organizationId, string ownerPersonId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(ownerPersonId);
        return ReadAsync(connection => QueryList(connection, null,
            "SELECT h.organization_id, h.hook_id, h.owner_person_id, h.machine_id, h.source, h.label, h.created_by_device_id, h.created_at, h.last_received_at, " +
            "(SELECT COUNT(*) FROM hook_events e WHERE e.hook_id = h.hook_id AND e.state = $pending) FROM hooks h " +
            "WHERE h.organization_id = $org AND h.owner_person_id = $owner ORDER BY h.created_at, h.hook_id",
            static reader => ReadHook(reader) with { Pending = reader.GetInt32(9) }, ("$org", organizationId), ("$owner", ownerPersonId), ("$pending", HookEventStates.Pending)),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteHookAsync(string organizationId, string hookId, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(hookId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction, "DELETE FROM hook_events WHERE organization_id = $org AND hook_id = $id", ("$org", organizationId), ("$id", hookId));
            if (Execute(connection, transaction, "DELETE FROM hooks WHERE organization_id = $org AND hook_id = $id", ("$org", organizationId), ("$id", hookId)) == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<(string DeliveryId, bool Duplicate)> AddHookEventAsync(HookEventRecord hookEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hookEvent);
        return WriteAsync((connection, transaction) =>
        {
            // The same event pushed again is the delivery received first, whatever its state.
            var known = QuerySingle(connection, transaction, "SELECT delivery_id FROM hook_events WHERE hook_id = $hook AND event_key = $key",
                static reader => reader.GetString(0), ("$hook", hookEvent.HookId), ("$key", hookEvent.EventKey));
            if (known is not null) return (known, true);
            Execute(connection, transaction,
                "INSERT INTO hook_events (organization_id, delivery_id, hook_id, machine_id, event_key, received_at, expires_at, state, message, answered_at, headers, body) " +
                "VALUES ($org, $id, $hook, $machine, $key, $received, $expires, $state, NULL, NULL, $headers, $body)",
                ("$org", hookEvent.OrganizationId), ("$id", hookEvent.DeliveryId), ("$hook", hookEvent.HookId), ("$machine", hookEvent.MachineId), ("$key", hookEvent.EventKey),
                ("$received", ToTime(hookEvent.ReceivedAt)), ("$expires", ToTime(hookEvent.ExpiresAt)), ("$state", hookEvent.State), ("$headers", hookEvent.Headers),
                ("$body", hookEvent.Body));
            Execute(connection, transaction, "UPDATE hooks SET last_received_at = $at WHERE hook_id = $hook", ("$at", ToTime(hookEvent.ReceivedAt)), ("$hook", hookEvent.HookId));
            return (hookEvent.DeliveryId, false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HookEventRecord>> ListPendingHookEventsAsync(string organizationId, string machineId, DateTimeOffset now, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        return ReadAsync(connection => QueryList(connection, null,
            HookEventColumns + ", headers, body FROM hook_events WHERE machine_id = $machine AND organization_id = $org AND state = $pending AND expires_at > $now " +
            "ORDER BY received_at, delivery_id LIMIT $limit",
            static reader => ReadHookEvent(reader) with
            {
                Headers = GetNullableString(reader, 10), Body = reader.IsDBNull(11) ? null : (byte[])reader.GetValue(11),
            },
            ("$org", organizationId), ("$machine", machineId), ("$pending", HookEventStates.Pending), ("$now", ToTime(now)), ("$limit", Math.Clamp(limit, 1, 1000))),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HookEventRecord>> ListPendingHookEventsAsync(string organizationId, string machineId, DateTimeOffset now, DateTimeOffset afterReceivedAt,
        string afterDeliveryId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentNullException.ThrowIfNull(afterDeliveryId);
        return ReadAsync(connection => QueryList(connection, null,
            HookEventColumns + ", headers, body FROM hook_events WHERE machine_id = $machine AND organization_id = $org AND state = $pending AND expires_at > $now " +
            "AND (received_at > $after OR (received_at = $after AND delivery_id > $afterId)) ORDER BY received_at, delivery_id LIMIT $limit",
            static reader => ReadHookEvent(reader) with
            {
                Headers = GetNullableString(reader, 10), Body = reader.IsDBNull(11) ? null : (byte[])reader.GetValue(11),
            },
            ("$org", organizationId), ("$machine", machineId), ("$pending", HookEventStates.Pending), ("$now", ToTime(now)), ("$after", ToTime(afterReceivedAt)),
            ("$afterId", afterDeliveryId), ("$limit", Math.Clamp(limit, 1, 1000))),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> AnswerHookEventAsync(string organizationId, string machineId, string deliveryId, string state, string? message, DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentException.ThrowIfNullOrEmpty(deliveryId);
        ArgumentException.ThrowIfNullOrEmpty(state);
        return WriteAsync((connection, transaction) => Execute(connection, transaction,
            "UPDATE hook_events SET state = $state, message = $message, answered_at = $at, headers = NULL, body = NULL " +
            "WHERE organization_id = $org AND machine_id = $machine AND delivery_id = $id AND state = $pending",
            ("$state", state), ("$message", message), ("$at", ToTime(at)), ("$org", organizationId), ("$machine", machineId), ("$id", deliveryId),
            ("$pending", HookEventStates.Pending)) > 0, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HookEventRecord>> ListHookEventsAsync(string organizationId, string hookId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(hookId);
        return ReadAsync(connection => QueryList(connection, null,
            HookEventColumns + " FROM hook_events WHERE organization_id = $org AND hook_id = $hook ORDER BY received_at DESC, delivery_id DESC LIMIT $limit",
            ReadHookEvent, ("$org", organizationId), ("$hook", hookId), ("$limit", Math.Clamp(limit, 1, 1000))), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<int> ExpireHookEventsAsync(DateTimeOffset now, DateTimeOffset forgetBefore, CancellationToken cancellationToken = default)
        => WriteAsync((connection, transaction) =>
        {
            var expired = Execute(connection, transaction,
                "UPDATE hook_events SET state = $expired, message = 'The machine did not take it in time.', headers = NULL, body = NULL " +
                "WHERE state = $pending AND expires_at <= $now",
                ("$expired", HookEventStates.Expired), ("$pending", HookEventStates.Pending), ("$now", ToTime(now)));
            Execute(connection, transaction, "DELETE FROM hook_events WHERE state <> $pending AND received_at < $before",
                ("$pending", HookEventStates.Pending), ("$before", ToTime(forgetBefore)));
            return expired;
        }, cancellationToken);

    private static HookRecord ReadHook(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            FromTime(reader.GetInt64(7)), GetNullableTime(reader, 8));

    private static HookEventRecord ReadHookEvent(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), FromTime(reader.GetInt64(5)), FromTime(reader.GetInt64(6)),
            reader.GetString(7), GetNullableString(reader, 8), GetNullableTime(reader, 9), null, null);
}
