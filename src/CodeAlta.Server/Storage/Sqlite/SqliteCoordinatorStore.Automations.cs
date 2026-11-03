using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// The automations of the organization and their runs (migrations 0007 and 0008).
public sealed partial class SqliteCoordinatorStore
{
    private const string AutomationColumns = "SELECT organization_id, automation_id, machine_id, owner_person_id, number, removed, enabled, triggers, payload, signature, " +
        "signer_device_id, updated_at, delivery_state, delivery_message, next_due FROM automations";

    private const string AutomationRunColumns = "SELECT organization_id, run_id, automation_id, machine_id, number, trigger, status, occurrence, started_at, ended_at, " +
        "lease_until, session_id, message, updated_at, input_tokens, output_tokens, cost, cost_unit FROM automation_runs";

    /// <inheritdoc />
    public ValueTask<bool> PutAutomationAsync(AutomationRecord automation, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            // A version replaces only an older one: a replay of what the person signed before changes nothing.
            var changed = Execute(connection, transaction,
                "INSERT INTO automations (organization_id, automation_id, machine_id, owner_person_id, number, removed, enabled, triggers, payload, signature, " +
                "signer_device_id, updated_at, delivery_state, delivery_message) " +
                "VALUES ($org, $id, $machine, $owner, $number, $removed, $enabled, $triggers, $payload, $signature, $signer, $at, $state, $message) " +
                "ON CONFLICT (organization_id, automation_id, machine_id) DO UPDATE SET owner_person_id = excluded.owner_person_id, number = excluded.number, " +
                "removed = excluded.removed, enabled = excluded.enabled, triggers = excluded.triggers, payload = excluded.payload, signature = excluded.signature, " +
                "signer_device_id = excluded.signer_device_id, updated_at = excluded.updated_at, delivery_state = excluded.delivery_state, " +
                "delivery_message = excluded.delivery_message WHERE automations.number < excluded.number",
                ("$org", automation.OrganizationId), ("$id", automation.AutomationId), ("$machine", automation.MachineId), ("$owner", automation.OwnerPersonId),
                ("$number", automation.Number), ("$removed", automation.Removed), ("$enabled", automation.Enabled), ("$triggers", automation.Triggers),
                ("$payload", automation.Payload), ("$signature", automation.Signature), ("$signer", automation.SignerDeviceId), ("$at", ToTime(automation.UpdatedAt)),
                ("$state", automation.DeliveryState), ("$message", automation.DeliveryMessage));
            if (changed == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AutomationRecord?> GetAutomationAsync(string organizationId, string automationId, string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        return ReadAsync(connection => QuerySingle(connection, null, AutomationColumns + " WHERE organization_id = $org AND automation_id = $id AND machine_id = $machine",
            ReadAutomation, ("$org", organizationId), ("$id", automationId), ("$machine", machineId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AutomationRecord>> ListAutomationsAsync(string organizationId, string ownerPersonId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(ownerPersonId);
        return ReadAsync(connection => QueryList(connection, null,
            AutomationColumns + " WHERE organization_id = $org AND owner_person_id = $owner ORDER BY automation_id, machine_id",
            ReadAutomation, ("$org", organizationId), ("$owner", ownerPersonId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AutomationRecord>> ListPendingAutomationsAsync(string organizationId, string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        return ReadAsync(connection => QueryList(connection, null,
            AutomationColumns + " WHERE machine_id = $machine AND organization_id = $org AND delivery_state = $pending ORDER BY automation_id",
            ReadAutomation, ("$org", organizationId), ("$machine", machineId), ("$pending", AutomationDeliveryStates.Pending)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> AcknowledgeAutomationAsync(string organizationId, string automationId, string machineId, long number, string state, string? message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentException.ThrowIfNullOrEmpty(state);
        return WriteAsync((connection, transaction) => Execute(connection, transaction,
            "UPDATE automations SET delivery_state = $state, delivery_message = $message " +
            "WHERE organization_id = $org AND automation_id = $id AND machine_id = $machine AND number = $number",
            ("$org", organizationId), ("$id", automationId), ("$machine", machineId), ("$number", number), ("$state", state), ("$message", message)) > 0, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAutomationAsync(string organizationId, string automationId, string machineId, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (Execute(connection, transaction, "DELETE FROM automations WHERE organization_id = $org AND automation_id = $id AND machine_id = $machine",
                ("$org", organizationId), ("$id", automationId), ("$machine", machineId)) == 0)
                return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AutomationClaimOutcome> ClaimAutomationOccurrenceAsync(AutomationRunRecord claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrEmpty(claim.Occurrence);
        return WriteAsync((connection, transaction) =>
        {
            var held = QuerySingle(connection, transaction, AutomationRunColumns + " WHERE organization_id = $org AND automation_id = $id AND occurrence = $occurrence",
                ReadAutomationRun, ("$org", claim.OrganizationId), ("$id", claim.AutomationId), ("$occurrence", claim.Occurrence));
            if (held is not null)
            {
                if (held.RunId != claim.RunId || held.MachineId != claim.MachineId) return new AutomationClaimOutcome(false, held);
                // The same run claims again while it works: its lease is renewed, never once it ended.
                if (held.EndedAt is null)
                    Execute(connection, transaction, "UPDATE automation_runs SET lease_until = $lease, updated_at = $at WHERE organization_id = $org AND run_id = $run",
                        ("$org", claim.OrganizationId), ("$run", claim.RunId), ("$lease", ToNullableTime(claim.LeaseUntil)), ("$at", ToTime(claim.UpdatedAt)));
                return new AutomationClaimOutcome(true, null);
            }
            // A run id is the machine's: one reported already for this run, without its due time, takes it now.
            if (QuerySingle(connection, transaction, AutomationRunColumns + " WHERE organization_id = $org AND run_id = $run", ReadAutomationRun,
                    ("$org", claim.OrganizationId), ("$run", claim.RunId)) is { } known)
            {
                if (known.MachineId != claim.MachineId || known.AutomationId != claim.AutomationId) return new AutomationClaimOutcome(false, known);
                Execute(connection, transaction, "UPDATE automation_runs SET occurrence = $occurrence, lease_until = $lease, updated_at = $at WHERE organization_id = $org AND run_id = $run",
                    ("$org", claim.OrganizationId), ("$run", claim.RunId), ("$occurrence", claim.Occurrence), ("$lease", ToNullableTime(claim.LeaseUntil)),
                    ("$at", ToTime(claim.UpdatedAt)));
                return new AutomationClaimOutcome(true, null);
            }

            InsertRun(connection, transaction, claim);
            return new AutomationClaimOutcome(true, null);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<int> RecordAutomationRunsAsync(IReadOnlyList<AutomationRunRecord> runs, int keep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keep);
        return WriteAsync((connection, transaction) =>
        {
            var written = 0;
            var touched = new HashSet<(string, string)>();
            foreach (var run in runs)
            {
                var known = QuerySingle(connection, transaction, AutomationRunColumns + " WHERE organization_id = $org AND run_id = $run", ReadAutomationRun,
                    ("$org", run.OrganizationId), ("$run", run.RunId));
                if (known is null)
                {
                    InsertRun(connection, transaction, run);
                }
                else
                {
                    // A machine reports its own runs only; the due time a claim took stays with it.
                    if (known.MachineId != run.MachineId || known.AutomationId != run.AutomationId) continue;
                    Execute(connection, transaction,
                        "UPDATE automation_runs SET number = $number, trigger = $trigger, status = $status, started_at = $started, ended_at = $ended, " +
                        "lease_until = $lease, session_id = $session, message = $message, updated_at = $at, input_tokens = $input, output_tokens = $output, " +
                        "cost = $cost, cost_unit = $unit WHERE organization_id = $org AND run_id = $run",
                        ("$org", run.OrganizationId), ("$run", run.RunId), ("$number", run.Number), ("$trigger", run.Trigger), ("$status", run.Status),
                        ("$started", ToTime(run.StartedAt)), ("$ended", ToNullableTime(run.EndedAt)), ("$lease", ToNullableTime(run.LeaseUntil ?? known.LeaseUntil)),
                        ("$session", run.SessionId), ("$message", run.Message), ("$at", ToTime(run.UpdatedAt)), ("$input", run.InputTokens),
                        ("$output", run.OutputTokens), ("$cost", run.Cost), ("$unit", run.CostUnit));
                }

                touched.Add((run.OrganizationId, run.AutomationId));
                written++;
            }

            foreach (var (organization, automation) in touched)
            {
                Execute(connection, transaction,
                    "DELETE FROM automation_runs WHERE organization_id = $org AND automation_id = $id AND run_id NOT IN " +
                    "(SELECT run_id FROM automation_runs WHERE organization_id = $org AND automation_id = $id ORDER BY started_at DESC LIMIT $keep)",
                    ("$org", organization), ("$id", automation), ("$keep", keep));
            }

            return written;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetAutomationNextDueAsync(string organizationId, string machineId, IReadOnlyList<(string AutomationId, DateTimeOffset? NextDue)> schedules,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentNullException.ThrowIfNull(schedules);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            foreach (var (automationId, nextDue) in schedules)
                Execute(connection, transaction, "UPDATE automations SET next_due = $due WHERE organization_id = $org AND automation_id = $id AND machine_id = $machine",
                    ("$org", organizationId), ("$id", automationId), ("$machine", machineId), ("$due", ToNullableTime(nextDue)));
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AutomationRunRecord>> ListAutomationRunsAsync(string organizationId, string automationId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync(connection => QueryList(connection, null,
            AutomationRunColumns + " WHERE organization_id = $org AND automation_id = $id ORDER BY started_at DESC, run_id DESC LIMIT $limit",
            ReadAutomationRun, ("$org", organizationId), ("$id", automationId), ("$limit", limit)), cancellationToken);
    }

    private static void InsertRun(SqliteConnection connection, SqliteTransaction transaction, AutomationRunRecord run)
        => Execute(connection, transaction,
            "INSERT INTO automation_runs (organization_id, run_id, automation_id, machine_id, number, trigger, status, occurrence, started_at, ended_at, lease_until, " +
            "session_id, message, updated_at, input_tokens, output_tokens, cost, cost_unit) " +
            "VALUES ($org, $run, $id, $machine, $number, $trigger, $status, $occurrence, $started, $ended, $lease, $session, $message, $at, $input, $output, $cost, $unit)",
            ("$org", run.OrganizationId), ("$run", run.RunId), ("$id", run.AutomationId), ("$machine", run.MachineId), ("$number", run.Number), ("$trigger", run.Trigger),
            ("$status", run.Status), ("$occurrence", run.Occurrence), ("$started", ToTime(run.StartedAt)), ("$ended", ToNullableTime(run.EndedAt)),
            ("$lease", ToNullableTime(run.LeaseUntil)), ("$session", run.SessionId), ("$message", run.Message), ("$at", ToTime(run.UpdatedAt)),
            ("$input", run.InputTokens), ("$output", run.OutputTokens), ("$cost", run.Cost), ("$unit", run.CostUnit));

    private static AutomationRecord ReadAutomation(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5) != 0, reader.GetInt64(6) != 0,
            reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), FromTime(reader.GetInt64(11)), reader.GetString(12), GetNullableString(reader, 13))
        {
            NextDue = GetNullableTime(reader, 14),
        };

    private static AutomationRunRecord ReadAutomationRun(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6),
            GetNullableString(reader, 7), FromTime(reader.GetInt64(8)), GetNullableTime(reader, 9), GetNullableTime(reader, 10), GetNullableString(reader, 11),
            GetNullableString(reader, 12), FromTime(reader.GetInt64(13)))
        {
            InputTokens = reader.IsDBNull(14) ? null : reader.GetInt64(14), OutputTokens = reader.IsDBNull(15) ? null : reader.GetInt64(15),
            Cost = reader.IsDBNull(16) ? null : reader.GetDouble(16), CostUnit = GetNullableString(reader, 17),
        };
}
