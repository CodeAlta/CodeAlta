using System.Globalization;
using System.Text;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Server.Storage.Sqlite;

public sealed partial class SqliteCoordinatorStore
{
    private const string StatisticsStream = "statistics";

    /// <summary>The separator of the keys of a statistics row besides its provider and model.</summary>
    internal const char StatisticsKeySeparator = '\u001f';

    /// <inheritdoc />
    public ValueTask<StatisticsPublication> PublishStatisticsAsync(string deviceId, string organizationId, long sequence, string generation,
        IReadOnlyList<StatisticsSetRecord> sets, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(generation);
        ArgumentNullException.ThrowIfNull(sets);
        return WriteAsync((connection, transaction) =>
        {
            var held = QuerySingle(connection, transaction, "SELECT generation FROM statistics_publishers WHERE device_id = $device", static reader => reader.GetString(0),
                ("$device", deviceId));
            if (!string.Equals(held, generation, StringComparison.Ordinal))
            {
                // A machine that lost what it published starts again: what it published before is replaced, not kept beside.
                Execute(connection, transaction, "DELETE FROM statistics_rows WHERE device_id = $device", ("$device", deviceId));
                Execute(connection, transaction, "DELETE FROM statistics_sets WHERE device_id = $device", ("$device", deviceId));
            }

            Execute(connection, transaction,
                "INSERT INTO statistics_publishers (device_id, organization_id, generation, published_at) VALUES ($device, $org, $generation, $at) " +
                "ON CONFLICT (device_id) DO UPDATE SET generation = excluded.generation, published_at = excluded.published_at",
                ("$device", deviceId), ("$org", organizationId), ("$generation", generation), ("$at", ToTime(now)));

            var outcomes = new List<(string, StatisticsSetOutcome)>(sets.Count);
            foreach (var set in sets)
            {
                var kept = QuerySingle(connection, transaction, "SELECT epoch, version FROM statistics_sets WHERE device_id = $device AND session_id = $session",
                    static reader => (Epoch: reader.GetInt64(0), Version: reader.GetInt64(1)), ("$device", deviceId), ("$session", set.SessionId));
                if (kept != default)
                {
                    if (set.Epoch < kept.Epoch)
                    {
                        outcomes.Add((set.SessionId, StatisticsSetOutcome.Stale));
                        continue;
                    }

                    if (set.Epoch == kept.Epoch && set.Version <= kept.Version)
                    {
                        outcomes.Add((set.SessionId, StatisticsSetOutcome.Unchanged));
                        continue;
                    }
                }

                // The rows of the set replace those held: a row that disappeared goes away with them.
                Execute(connection, transaction, "DELETE FROM statistics_rows WHERE device_id = $device AND session_id = $session", ("$device", deviceId),
                    ("$session", set.SessionId));
                using (var insert = Create(connection, transaction,
                    "INSERT INTO statistics_rows (device_id, session_id, family, q, provider, model, keys, measure, value) " +
                    "VALUES ($device, $session, $family, $q, $provider, $model, $keys, $measure, $value) " +
                    "ON CONFLICT (device_id, session_id, family, q, provider, model, keys, measure) DO UPDATE SET value = " +
                    "CASE WHEN $max = 1 THEN MAX(value, excluded.value) ELSE value + excluded.value END",
                    ("$device", deviceId), ("$session", set.SessionId), ("$family", ""), ("$q", 0L), ("$provider", ""), ("$model", ""), ("$keys", ""),
                    ("$measure", ""), ("$value", 0L), ("$max", 0L)))
                {
                    foreach (var row in set.Rows)
                    {
                        insert.Parameters["$family"].Value = row.Family;
                        insert.Parameters["$q"].Value = row.Quarter;
                        insert.Parameters["$provider"].Value = row.Provider ?? string.Empty;
                        insert.Parameters["$model"].Value = row.Model ?? string.Empty;
                        insert.Parameters["$keys"].Value = row.Keys is { Count: > 0 } keys ? string.Join(StatisticsKeySeparator, keys) : string.Empty;
                        foreach (var (measure, value) in row.Measures)
                        {
                            insert.Parameters["$measure"].Value = measure;
                            insert.Parameters["$value"].Value = value;
                            insert.Parameters["$max"].Value = FleetStatisticsFamilies.MaxMeasures.Contains(measure) ? 1L : 0L;
                            insert.ExecuteNonQuery();
                        }
                    }
                }

                Execute(connection, transaction,
                    "INSERT INTO statistics_sets (device_id, session_id, organization_id, epoch, version, project_key, space_key, received_at) " +
                    "VALUES ($device, $session, $org, $epoch, $version, $project, $space, $at) " +
                    "ON CONFLICT (device_id, session_id) DO UPDATE SET epoch = excluded.epoch, version = excluded.version, project_key = excluded.project_key, " +
                    "space_key = excluded.space_key, received_at = excluded.received_at",
                    ("$device", deviceId), ("$session", set.SessionId), ("$org", organizationId), ("$epoch", set.Epoch), ("$version", set.Version),
                    ("$project", set.ProjectKey), ("$space", set.SpaceKey), ("$at", ToTime(now)));
                outcomes.Add((set.SessionId, StatisticsSetOutcome.Applied));
            }

            var cursor = ReadCursor(connection, transaction, deviceId, StatisticsStream);
            if (sequence > cursor)
            {
                WriteCursor(connection, transaction, deviceId, StatisticsStream, sequence);
                cursor = sequence;
            }

            return new StatisticsPublication(cursor, outcomes);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> WithdrawStatisticsAsync(string deviceId, IReadOnlyList<string> sessionIds, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(sessionIds);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync<IReadOnlyList<string>>((connection, transaction) =>
        {
            var removed = new List<string>();
            foreach (var id in sessionIds.Distinct(StringComparer.Ordinal))
            {
                Execute(connection, transaction, "DELETE FROM statistics_rows WHERE device_id = $device AND session_id = $session", ("$device", deviceId), ("$session", id));
                if (Execute(connection, transaction, "DELETE FROM statistics_sets WHERE device_id = $device AND session_id = $session", ("$device", deviceId), ("$session", id)) == 1)
                {
                    removed.Add(id);
                }
            }

            if (removed.Count > 0) AppendAudit(connection, transaction, audit with { Details = IdsDetails("sessions", removed) });
            return removed;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StatisticsSumRecord>> SumStatisticsAsync(StatisticsSumQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrEmpty(query.OrganizationId);
        if (query.Buckets.Count == 0 || query.Measures.Count == 0) return ValueTask.FromResult<IReadOnlyList<StatisticsSumRecord>>([]);
        return ReadAsync<IReadOnlyList<StatisticsSumRecord>>(connection =>
        {
            var parameters = new List<(string, object?)> { ("$org", query.OrganizationId), ("$person", query.OwnerPersonId) };
            var sql = new StringBuilder("WITH buckets (b, lo, hi) AS (VALUES ");
            for (var i = 0; i < query.Buckets.Count; i++)
            {
                // Integers the server computed: written as they are, so that a year of days is one statement of few parameters.
                if (i > 0) sql.Append(", ");
                sql.Append(CultureInfo.InvariantCulture, $"({i}, {query.Buckets[i].From}, {query.Buckets[i].To})");
            }

            sql.Append("), measures (family, measure) AS (VALUES ");
            for (var i = 0; i < query.Measures.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append(CultureInfo.InvariantCulture, $"($f{i}, $m{i})");
                parameters.Add(($"$f{i}", query.Measures[i].Family));
                parameters.Add(($"$m{i}", query.Measures[i].Measure));
            }

            var dimensions = new List<string>();
            if (query.ByMachine) dimensions.Add("r.device_id");
            if (query.ByProject) dimensions.Add("COALESCE(s.project_key, '')");
            if (query.ByProvider) dimensions.Add("r.provider");
            if (query.ByModel) dimensions.Add("r.model");
            var unit = $"CASE WHEN r.family = '{FleetStatisticsFamilies.Cost}' THEN r.keys ELSE '' END";
            var group = string.Join(", ", new[] { "bk.b" }.Concat(dimensions).Concat(["r.family", "r.measure", unit]));
            sql.Append(") SELECT ").Append(group).Append(", SUM(r.value) FROM statistics_rows r ")
                .Append("JOIN measures m ON m.family = r.family AND m.measure = r.measure ")
                .Append("JOIN buckets bk ON r.q >= bk.lo AND r.q < bk.hi ")
                .Append("JOIN statistics_sets s ON s.device_id = r.device_id AND s.session_id = r.session_id ")
                .Append("JOIN devices d ON d.id = r.device_id ")
                .Append("WHERE s.organization_id = $org AND ($person IS NULL OR d.owner_person_id = $person) AND r.q >= $lo AND r.q < $hi ")
                .Append("GROUP BY ").Append(group);
            parameters.Add(("$lo", query.Buckets.Min(static bucket => bucket.From)));
            parameters.Add(("$hi", query.Buckets.Max(static bucket => bucket.To)));
            return QueryList(connection, null, sql.ToString(), reader =>
            {
                var column = 1;
                string? Next(bool grouped) => grouped ? reader.GetString(column++) : null;
                var machine = Next(query.ByMachine);
                var project = Next(query.ByProject);
                var provider = Next(query.ByProvider);
                var model = Next(query.ByModel);
                return new StatisticsSumRecord(reader.GetInt32(0), machine, project, provider, model, reader.GetString(column), reader.GetString(column + 1),
                    reader.GetString(column + 2), reader.GetInt64(column + 3));
            }, [.. parameters]);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StatisticsPublisherRecord>> ListStatisticsPublishersAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null,
            "SELECT p.device_id, p.published_at, (SELECT COUNT(*) FROM statistics_sets s WHERE s.device_id = p.device_id) FROM statistics_publishers p " +
            "WHERE p.organization_id = $org ORDER BY p.device_id",
            static reader => new StatisticsPublisherRecord(reader.GetString(0), FromTime(reader.GetInt64(1)), reader.GetInt64(2)), ("$org", organizationId)),
            cancellationToken);
    }
}
