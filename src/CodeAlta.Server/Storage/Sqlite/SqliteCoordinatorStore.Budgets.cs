using System.Text;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Services;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// The budgets, their allowances and holds, the notifications of people and the health of machines (migration 0012).
public sealed partial class SqliteCoordinatorStore : IBudgetStore
{
    private const string BudgetColumns = "SELECT id, organization_id, owner_person_id, number, removed, payload, signature, signer_device_id, updated_at FROM budgets";

    private const string HoldColumns = "SELECT id, organization_id, budget_id, number, period_start, machine_id, run_id, nonce, title, status, requested_at, continue_payload, " +
        "continue_signature, decided_by_device_id, decided_at FROM budget_holds";

    private const string NotificationColumns = "SELECT id, organization_id, person_id, dedupe_key, kind, severity, budget_id, machine_id, problem, subject, detail, created_at, " +
        "resolved_at, read_at FROM notifications";

    private const string HealthColumns = "SELECT device_id, kind, subject, detail, first_seen, last_seen, notification_id FROM machine_health";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<BudgetRecord>> ListBudgetsAsync(string organizationId, string? ownerPersonId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => ownerPersonId is null
            ? QueryList(connection, null, BudgetColumns + " WHERE organization_id = $org ORDER BY id", ReadBudget, ("$org", organizationId))
            : QueryList(connection, null, BudgetColumns + " WHERE organization_id = $org AND owner_person_id = $owner ORDER BY id", ReadBudget,
                ("$org", organizationId), ("$owner", ownerPersonId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<BudgetRecord?> GetBudgetAsync(string organizationId, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ReadAsync(connection => QuerySingle(connection, null, BudgetColumns + " WHERE organization_id = $org AND id = $id", ReadBudget,
            ("$org", organizationId), ("$id", id)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> PutBudgetAsync(BudgetRecord budget, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var held = QuerySingle(connection, transaction, BudgetColumns + " WHERE id = $id", ReadBudget, ("$id", budget.Id));
            if (held is not null && (held.OrganizationId != budget.OrganizationId || held.OwnerPersonId != budget.OwnerPersonId || held.Number >= budget.Number)) return false;
            if (held is null && budget.Removed) return false;
            Execute(connection, transaction,
                "INSERT INTO budgets (id, organization_id, owner_person_id, number, removed, payload, signature, signer_device_id, updated_at) " +
                "VALUES ($id, $org, $owner, $number, $removed, $payload, $signature, $signer, $at) " +
                "ON CONFLICT (id) DO UPDATE SET number = excluded.number, removed = excluded.removed, payload = excluded.payload, signature = excluded.signature, " +
                "signer_device_id = excluded.signer_device_id, updated_at = excluded.updated_at",
                ("$id", budget.Id), ("$org", budget.OrganizationId), ("$owner", budget.OwnerPersonId), ("$number", budget.Number), ("$removed", budget.Removed ? 1L : 0L),
                ("$payload", budget.Payload), ("$signature", budget.Signature), ("$signer", budget.SignerDeviceId), ("$at", ToTime(budget.UpdatedAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, long>> SumBudgetUsageAsync(BudgetUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadAsync(connection => SumUsage(connection, null, query), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<string, long>> ListAllowancesAsync(string budgetId, long periodStart, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetId);
        return ReadAsync(connection => Allowances(connection, null, budgetId, periodStart), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<(long Granted, bool Exhausted)> GrantAllowanceAsync(BudgetAllowanceAsk ask, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        return WriteAsync((connection, transaction) =>
        {
            // The facts and the allowances are read in the transaction that writes: one writer, so two machines asking at once
            // never get the same part of the limit.
            var granted = Allowances(connection, transaction, ask.BudgetId, ask.PeriodStart);
            var used = SumUsage(connection, transaction, ask.Usage);
            var (holds, exhausted) = BudgetAllowances.Grant(ask.Limit, granted, used, ask.DeviceId, ask.Spent, ask.Needed, ask.Share);
            if (holds != granted.GetValueOrDefault(ask.DeviceId))
            {
                Execute(connection, transaction,
                    "INSERT INTO budget_allowances (budget_id, period_start, device_id, granted, updated_at) VALUES ($budget, $period, $device, $granted, $at) " +
                    "ON CONFLICT (budget_id, period_start, device_id) DO UPDATE SET granted = excluded.granted, updated_at = excluded.updated_at",
                    ("$budget", ask.BudgetId), ("$period", ask.PeriodStart), ("$device", ask.DeviceId), ("$granted", holds), ("$at", ToTime(now)));
            }

            return (holds, exhausted);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<BudgetHoldRecord> RequestBudgetHoldAsync(BudgetHoldRecord hold, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (QuerySingle(connection, transaction, HoldColumns + " WHERE budget_id = $budget AND machine_id = $machine AND run_id = $run", ReadHold,
                    ("$budget", hold.BudgetId), ("$machine", hold.MachineId), ("$run", hold.RunId)) is { } held)
                return held;
            Execute(connection, transaction,
                "INSERT INTO budget_holds (id, organization_id, budget_id, number, period_start, machine_id, run_id, nonce, title, status, requested_at) " +
                "VALUES ($id, $org, $budget, $number, $period, $machine, $run, $nonce, $title, $status, $at)",
                ("$id", hold.Id), ("$org", hold.OrganizationId), ("$budget", hold.BudgetId), ("$number", hold.Number), ("$period", hold.PeriodStart),
                ("$machine", hold.MachineId), ("$run", hold.RunId), ("$nonce", hold.Nonce), ("$title", hold.Title), ("$status", hold.Status), ("$at", ToTime(hold.RequestedAt)));
            AppendAudit(connection, transaction, audit);
            return hold;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<BudgetHoldRecord?> GetBudgetHoldAsync(string organizationId, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ReadAsync(connection => QuerySingle(connection, null, HoldColumns + " WHERE organization_id = $org AND id = $id", ReadHold,
            ("$org", organizationId), ("$id", id)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<BudgetHoldRecord>> ListBudgetHoldsAsync(string organizationId, IReadOnlyCollection<string> budgetIds, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentNullException.ThrowIfNull(budgetIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync(connection =>
        {
            var all = new List<BudgetHoldRecord>();
            foreach (var budget in budgetIds)
            {
                all.AddRange(QueryList(connection, null, HoldColumns + " WHERE organization_id = $org AND budget_id = $budget ORDER BY requested_at DESC LIMIT $limit",
                    ReadHold, ("$org", organizationId), ("$budget", budget), ("$limit", limit)));
            }

            return (IReadOnlyList<BudgetHoldRecord>)[.. all.OrderByDescending(static hold => hold.RequestedAt).ThenBy(static hold => hold.Id, StringComparer.Ordinal).Take(limit)];
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> DecideBudgetHoldAsync(string organizationId, string id, string status, string? payload, string? signature, string deviceId, DateTimeOffset at,
        AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(status);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (Execute(connection, transaction,
                    "UPDATE budget_holds SET status = $status, continue_payload = $payload, continue_signature = $signature, decided_by_device_id = $device, decided_at = $at " +
                    "WHERE organization_id = $org AND id = $id AND status = 'waiting'",
                    ("$org", organizationId), ("$id", id), ("$status", status), ("$payload", payload), ("$signature", signature), ("$device", deviceId), ("$at", ToTime(at))) == 0)
                return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> AddNotificationAsync(NotificationRecord notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return WriteAsync((connection, transaction) => Execute(connection, transaction,
            "INSERT INTO notifications (id, organization_id, person_id, dedupe_key, kind, severity, budget_id, machine_id, problem, subject, detail, created_at) " +
            "VALUES ($id, $org, $person, $key, $kind, $severity, $budget, $machine, $problem, $subject, $detail, $at) " +
            "ON CONFLICT (organization_id, person_id, dedupe_key) DO NOTHING",
            ("$id", notification.Id), ("$org", notification.OrganizationId), ("$person", notification.PersonId), ("$key", notification.DedupeKey), ("$kind", notification.Kind),
            ("$severity", notification.Severity), ("$budget", notification.BudgetId), ("$machine", notification.MachineId), ("$problem", notification.Problem),
            ("$subject", notification.Subject), ("$detail", notification.Detail), ("$at", ToTime(notification.CreatedAt))) == 1, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<NotificationRecord>> ListNotificationsAsync(string organizationId, string personId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync(connection => QueryList(connection, null,
            NotificationColumns + " WHERE organization_id = $org AND person_id = $person ORDER BY created_at DESC, id LIMIT $limit", ReadNotification,
            ("$org", organizationId), ("$person", personId), ("$limit", limit)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> ReadNotificationAsync(string organizationId, string personId, string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        return WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction, "UPDATE notifications SET read_at = COALESCE(read_at, $at) WHERE organization_id = $org AND person_id = $person AND id = $id",
                ("$org", organizationId), ("$person", personId), ("$id", id), ("$at", ToTime(at)));
            return QuerySingle(connection, transaction, "SELECT 1 FROM notifications WHERE organization_id = $org AND person_id = $person AND id = $id",
                static reader => true, ("$org", organizationId), ("$person", personId), ("$id", id));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask ResolveNotificationsAsync(IReadOnlyCollection<string> ids, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;
        await WriteAsync((connection, transaction) =>
        {
            foreach (var id in ids)
                Execute(connection, transaction, "UPDATE notifications SET resolved_at = COALESCE(resolved_at, $at) WHERE id = $id", ("$id", id), ("$at", ToTime(at)));
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<(IReadOnlyList<MachineHealthRecord> Current, IReadOnlyList<MachineHealthRecord> Ended)> ReportHealthAsync(string deviceId,
        IReadOnlyList<(string Kind, string Subject, string? Detail)> problems, IReadOnlyCollection<string> kinds, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(kinds);
        return WriteAsync<(IReadOnlyList<MachineHealthRecord>, IReadOnlyList<MachineHealthRecord>)>((connection, transaction) =>
        {
            var held = QueryList(connection, transaction, HealthColumns + " WHERE device_id = $device", ReadHealth, ("$device", deviceId));
            var reported = problems.Select(static problem => (problem.Kind, problem.Subject)).ToHashSet();
            var ended = held.Where(problem => kinds.Contains(problem.Kind) && !reported.Contains((problem.Kind, problem.Subject))).ToArray();
            foreach (var problem in ended)
                Execute(connection, transaction, "DELETE FROM machine_health WHERE device_id = $device AND kind = $kind AND subject = $subject",
                    ("$device", deviceId), ("$kind", problem.Kind), ("$subject", problem.Subject));
            foreach (var (kind, subject, detail) in problems)
            {
                Execute(connection, transaction,
                    "INSERT INTO machine_health (device_id, kind, subject, detail, first_seen, last_seen) VALUES ($device, $kind, $subject, $detail, $at, $at) " +
                    "ON CONFLICT (device_id, kind, subject) DO UPDATE SET detail = excluded.detail, last_seen = excluded.last_seen",
                    ("$device", deviceId), ("$kind", kind), ("$subject", subject), ("$detail", detail), ("$at", ToTime(now)));
            }

            var current = QueryList(connection, transaction, HealthColumns + " WHERE device_id = $device ORDER BY kind, subject", ReadHealth, ("$device", deviceId));
            return (current, ended);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask SetHealthNotificationAsync(string deviceId, string kind, string subject, string notificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrEmpty(notificationId);
        await WriteAsync((connection, transaction) => Execute(connection, transaction,
            "UPDATE machine_health SET notification_id = $notification WHERE device_id = $device AND kind = $kind AND subject = $subject",
            ("$device", deviceId), ("$kind", kind), ("$subject", subject), ("$notification", notificationId)), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, long> Allowances(SqliteConnection connection, SqliteTransaction? transaction, string budgetId, long periodStart)
        => QueryList(connection, transaction, "SELECT device_id, granted FROM budget_allowances WHERE budget_id = $budget AND period_start = $period",
                static reader => (Device: reader.GetString(0), Granted: reader.GetInt64(1)), ("$budget", budgetId), ("$period", periodStart))
            .ToDictionary(static pair => pair.Device, static pair => pair.Granted, StringComparer.Ordinal);

    // What the facts of the machines of the owner count of a budget's scope in its quarter hours, by machine.
    private static IReadOnlyDictionary<string, long> SumUsage(SqliteConnection connection, SqliteTransaction? transaction, BudgetUsageQuery query)
    {
        var sql = new StringBuilder("SELECT r.device_id, SUM(r.value) FROM statistics_rows r ")
            .Append("JOIN statistics_sets s ON s.device_id = r.device_id AND s.session_id = r.session_id ")
            .Append("JOIN devices d ON d.id = r.device_id ")
            .Append("WHERE s.organization_id = $org AND d.owner_person_id = $owner AND r.q >= $lo AND r.q < $hi ");
        sql.Append(query.Measure == FleetBudgetMeasures.Cost
            ? $"AND r.family = '{FleetStatisticsFamilies.Cost}' AND r.measure = 'total_micro' AND r.keys IN ('', 'usd') "
            : $"AND r.family = '{FleetStatisticsFamilies.Usage}' AND r.measure IN ('input_tokens', 'output_tokens') ");
        sql.Append(query.ScopeKind switch
        {
            FleetBudgetScopeKinds.Machine => "AND r.device_id = $scope ",
            FleetBudgetScopeKinds.Project => "AND s.project_key = $scope ",
            FleetBudgetScopeKinds.Space => "AND s.space_key = $scope ",
            FleetBudgetScopeKinds.Person => string.Empty,
            // The scopes of M4 count nothing yet.
            _ => "AND 0 ",
        });
        sql.Append("GROUP BY r.device_id");
        return QueryList(connection, transaction, sql.ToString(), static reader => (Device: reader.GetString(0), Sum: reader.GetInt64(1)),
                ("$org", query.OrganizationId), ("$owner", query.OwnerPersonId), ("$lo", query.FromQuarter), ("$hi", query.ToQuarter), ("$scope", query.ScopeId ?? string.Empty))
            .Where(static pair => pair.Sum != 0)
            .ToDictionary(static pair => pair.Device, static pair => pair.Sum, StringComparer.Ordinal);
    }

    private static BudgetRecord ReadBudget(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4) != 0, reader.GetString(5), reader.GetString(6),
            reader.GetString(7), FromTime(reader.GetInt64(8)));

    private static BudgetHoldRecord ReadHold(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            GetNullableString(reader, 8), reader.GetString(9), FromTime(reader.GetInt64(10)), GetNullableString(reader, 11), GetNullableString(reader, 12),
            GetNullableString(reader, 13), GetNullableTime(reader, 14));

    private static NotificationRecord ReadNotification(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), GetNullableString(reader, 6),
            GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9), GetNullableString(reader, 10), FromTime(reader.GetInt64(11)),
            GetNullableTime(reader, 12), GetNullableTime(reader, 13));

    private static MachineHealthRecord ReadHealth(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3), FromTime(reader.GetInt64(4)), FromTime(reader.GetInt64(5)),
            GetNullableString(reader, 6));
}
