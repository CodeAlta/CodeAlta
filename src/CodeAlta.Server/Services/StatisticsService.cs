using System.Globalization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The statistics of the fleet (<c>doc/fleet/statistics.md</c>): the sets of rows machines publish over their control connection,
/// kept as the highest version per machine and session, and the sums the REST API answers. Only facts are kept: counts and the
/// names of providers, models and tools, the blind index of a project, never content.
/// </summary>
public sealed class StatisticsService
{
    /// <summary>The most sets in one batch.</summary>
    public const int MaxSetsPerBatch = 500;

    /// <summary>The most rows in one batch.</summary>
    public const int MaxRowsPerBatch = 20_000;

    /// <summary>The most days of a query.</summary>
    public const int MaxDays = 400;

    /// <summary>The most groups of an answer.</summary>
    public const int MaxGroups = 500;

    private const int MaxIdLength = 128;
    private const int MaxTextLength = 128;
    private const int MaxKeys = 8;
    private const int MaxMeasures = 32;

    // The measures a query sums, and where each goes in its answer.
    private static readonly (string Family, string Measure)[] Summed =
    [
        (FleetStatisticsFamilies.Activity, "active_ms"), (FleetStatisticsFamilies.Activity, "runs_started"), (FleetStatisticsFamilies.Activity, "runs_failed"),
        (FleetStatisticsFamilies.Usage, "requests"), (FleetStatisticsFamilies.Usage, "input_tokens"), (FleetStatisticsFamilies.Usage, "output_tokens"),
        (FleetStatisticsFamilies.Usage, "cache_read_tokens"), (FleetStatisticsFamilies.Usage, "cache_write_tokens"), (FleetStatisticsFamilies.Usage, "reasoning_tokens"),
        (FleetStatisticsFamilies.Tools, "calls"), (FleetStatisticsFamilies.Tools, "failures"), (FleetStatisticsFamilies.Cost, "total_micro"),
    ];

    private readonly ICoordinatorStore _store;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="StatisticsService"/> class.</summary>
    /// <param name="store">The store.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public StatisticsService(ICoordinatorStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;
    }

    /// <summary>Gets the instruments of the publications; null for none.</summary>
    public Telemetry.ServerMetrics? Metrics { get; init; }

    /// <summary>Keeps the statistics sets a machine publishes.</summary>
    /// <param name="device">The device of the machine.</param>
    /// <param name="publication">The publication.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The acknowledged sequence and what happened to the sets.</returns>
    /// <exception cref="CoordinatorException">A set does not read (<see cref="CoordinatorErrorCodes.PublicationInvalid"/>).</exception>
    public async Task<FleetStatisticsPublishResult> PublishAsync(DeviceRecord device, FleetStatisticsPublish? publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        try
        {
            if (publication?.Sets is not { } sets || publication.Sequence <= 0) throw Invalid("statistics.publish needs a sequence from 1 and sets.");
            var generation = Text(publication.Generation, "generation", required: true, 64)!;
            if (sets.Count > MaxSetsPerBatch) throw Invalid($"statistics.publish takes at most {MaxSetsPerBatch} sets.");
            if (sets.Sum(static set => set?.Rows?.Count ?? 0) > MaxRowsPerBatch) throw Invalid($"statistics.publish takes at most {MaxRowsPerBatch} rows.");
            var records = new List<StatisticsSetRecord>(sets.Count);
            foreach (var set in sets)
            {
                if (set is null) throw Invalid("A set cannot be null.");
                if (set.Epoch < 0 || set.Version < 1) throw Invalid("The epoch of a set cannot be negative, and its version starts at 1.");
                var rows = new List<FleetStatisticsRow>(set.Rows?.Count ?? 0);
                foreach (var row in set.Rows ?? [])
                {
                    rows.Add(Row(row));
                }

                records.Add(new StatisticsSetRecord(Text(set.SessionId, "sessionId", required: true, MaxIdLength)!, set.Epoch, set.Version,
                    Text(set.ProjectKey, "projectKey", maxLength: 64), rows));
            }

            var result = await _store.PublishStatisticsAsync(device.Id, device.OrganizationId, publication.Sequence, generation, records, _time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            Metrics?.Publication("statistics", null);
            return new FleetStatisticsPublishResult(result.Ack, result.Outcomes.Count(static outcome => outcome.Outcome == StatisticsSetOutcome.Applied),
                result.Outcomes.Count(static outcome => outcome.Outcome == StatisticsSetOutcome.Unchanged),
                [.. result.Outcomes.Where(static outcome => outcome.Outcome == StatisticsSetOutcome.Stale).Select(static outcome => outcome.SessionId)]);
        }
        catch (CoordinatorException exception)
        {
            Metrics?.Publication("statistics", exception.Code);
            throw;
        }
    }

    /// <summary>Deletes the statistics sets of sessions a machine no longer publishes (made Private, or their space unlinked).</summary>
    /// <param name="device">The device of the machine.</param>
    /// <param name="withdrawal">The withdrawal.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The sessions whose sets were deleted.</returns>
    /// <exception cref="CoordinatorException">The withdrawal does not read (<see cref="CoordinatorErrorCodes.PublicationInvalid"/>).</exception>
    public async Task<FleetStatisticsWithdrawResult> WithdrawAsync(DeviceRecord device, FleetStatisticsWithdraw? withdrawal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (withdrawal?.SessionIds is not { } ids) throw Invalid("statistics.withdraw needs the ids of sessions.");
        if (ids.Count > MaxSetsPerBatch) throw Invalid($"statistics.withdraw takes at most {MaxSetsPerBatch} sessions.");
        var checkedIds = ids.Select(id => Text(id, "sessionIds", required: true, MaxIdLength)!).ToArray();
        var audit = new AuditEntry("statistics.withdraw", AuditEntry.Success, device.OwnerPersonId, device.Id, device.Id, null, null);
        var removed = await _store.WithdrawStatisticsAsync(device.Id, checkedIds, audit, cancellationToken).ConfigureAwait(false);
        Metrics?.Publication("statistics.withdraw", null);
        return new FleetStatisticsWithdrawResult(removed);
    }

    /// <summary>
    /// Sums the statistics of the calling device's person (<c>mine</c>): the machines that person owns, by machine, project key,
    /// provider, model, local day or month, over local days.
    /// </summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sums.</returns>
    /// <exception cref="CoordinatorException">The query does not read (<see cref="CoordinatorErrorCodes.StatisticsInvalid"/>), or the device has no person.</exception>
    public async Task<StatisticsQueryResponse> QueryAsync(DeviceRecord actor, StatisticsQueryRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (request is null) throw Query("A query needs a body.");
        var scope = request.Scope ?? "mine";
        // The person's own fleet; teams and the organization come with their access rules (doc/fleet/statistics.md, who sees what).
        if (scope != "mine") throw Query("The scope of a query is 'mine'.");
        if (actor.OwnerPersonId is not { } person) throw CoordinatorException.Forbidden(CoordinatorErrorCodes.StatisticsInvalid, "The statistics of a person are asked by a device of that person.");
        if (!DateOnly.TryParseExact(request.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(request.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to < from)
        {
            throw Query("A query needs its first and last days, yyyy-MM-dd, the last not before the first.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxDays) throw Query($"A query covers at most {MaxDays} days.");
        var zone = Zone(request.TimeZone);
        var groupBy = request.GroupBy?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (groupBy.Length > 3 || groupBy.Any(group => !StatisticsGroupBy.All.Contains(group)) || (groupBy.Contains(StatisticsGroupBy.Day) && groupBy.Contains(StatisticsGroupBy.Month)))
        {
            throw Query($"A query groups by at most three of {string.Join(", ", StatisticsGroupBy.All)}, and by days or months, not both.");
        }

        var limit = Math.Clamp(request.Limit ?? MaxGroups, 1, MaxGroups);
        var buckets = Buckets(from, to, zone, groupBy.Contains(StatisticsGroupBy.Day) ? StatisticsGroupBy.Day : groupBy.Contains(StatisticsGroupBy.Month) ? StatisticsGroupBy.Month : null);
        var sums = await _store.SumStatisticsAsync(new StatisticsSumQuery(actor.OrganizationId, person, [.. buckets.Select(static bucket => (bucket.From, bucket.To))],
            groupBy.Contains(StatisticsGroupBy.Machine), groupBy.Contains(StatisticsGroupBy.Project), groupBy.Contains(StatisticsGroupBy.Provider),
            groupBy.Contains(StatisticsGroupBy.Model), Summed), cancellationToken).ConfigureAwait(false);
        var byPeriod = groupBy.Contains(StatisticsGroupBy.Day) || groupBy.Contains(StatisticsGroupBy.Month);
        var totals = new Sums();
        var groups = new Dictionary<(string? Machine, string? Project, string? Provider, string? Model, string? Period), Sums>();
        foreach (var sum in sums)
        {
            totals.Add(sum);
            var key = (sum.Machine, sum.Project, sum.Provider, sum.Model, byPeriod ? buckets[sum.Bucket].Label : null);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new Sums();
            group.Add(sum);
        }

        var ordered = groupBy.Length == 0 ? [] : groups
            .OrderByDescending(static pair => pair.Value.Tokens)
            .ThenBy(static pair => pair.Key.Period, StringComparer.Ordinal).ThenBy(static pair => pair.Key.Machine, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Project, StringComparer.Ordinal).ThenBy(static pair => pair.Key.Provider, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Model, StringComparer.Ordinal)
            .ToArray();
        var devices = await _store.ListDevicesAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        var publishers = (await _store.ListStatisticsPublishersAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static publisher => publisher.DeviceId, StringComparer.Ordinal);
        var machines = devices
            .Where(device => device.OwnerPersonId == person && (device.Status != DeviceStatuses.Revoked || publishers.ContainsKey(device.Id)))
            .Select(device => publishers.TryGetValue(device.Id, out var published)
                ? new StatisticsMachineDto(device.Id, device.Name, device.Kind, published.PublishedAt, published.Sessions)
                : new StatisticsMachineDto(device.Id, device.Name, device.Kind, null, 0))
            .OrderBy(static machine => machine.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static machine => machine.Id, StringComparer.Ordinal)
            .ToArray();
        return new StatisticsQueryResponse(scope, request.From, request.To, zone.Id, groupBy, totals.ToDto(),
            [.. ordered.Take(limit).Select(static pair => new StatisticsGroupDto(pair.Key.Machine, pair.Key.Project, pair.Key.Provider, pair.Key.Model, pair.Key.Period, pair.Value.ToDto()))],
            machines, ordered.Length > limit);
    }

    // The ranges of quarter hours of the local days or months of the period, or of the whole period. Every offset in use is a
    // whole number of quarter hours, so a local day is an exact range of quarters, across changes of daylight saving time too.
    private static List<(long From, long To, string Label)> Buckets(DateOnly from, DateOnly to, TimeZoneInfo zone, string? period)
    {
        long Start(DateOnly day) => FleetStatisticsFamilies.QuarterOf(Local(day, zone));
        var buckets = new List<(long, long, string)>();
        switch (period)
        {
            case StatisticsGroupBy.Day:
                for (var day = from; day <= to; day = day.AddDays(1)) buckets.Add((Start(day), Start(day.AddDays(1)), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                break;
            case StatisticsGroupBy.Month:
                for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
                {
                    var first = month < from ? from : month;
                    var next = month.AddMonths(1);
                    buckets.Add((Start(first), Start(next > to ? to.AddDays(1) : next), month.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
                }

                break;
            default:
                buckets.Add((Start(from), Start(to.AddDays(1)), string.Empty));
                break;
        }

        return buckets;
    }

    // The start of a local day: midnight, or the first minute that exists when a change of time skips midnight.
    private static DateTimeOffset Local(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(15);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "UTC") return TimeZoneInfo.Utc;
        if (id.Length > 64) throw Query("A time zone is an IANA or Windows id.");
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw Query($"The time zone '{id}' is not known to the server.");
        }
    }

    private static FleetStatisticsRow Row(FleetStatisticsRow? row)
    {
        if (row is null) throw Invalid("A row cannot be null.");
        if (!FleetStatisticsFamilies.All.Contains(row.Family ?? string.Empty)) throw Invalid($"The family of a row is one of {string.Join(", ", FleetStatisticsFamilies.All)}.");
        if (row.Keys is { Count: > MaxKeys }) throw Invalid($"A row has at most {MaxKeys} keys.");
        if (row.Measures is not { Count: > 0 and <= MaxMeasures } measures) throw Invalid($"A row has from 1 to {MaxMeasures} measures.");
        var keys = row.Keys?.Select(key => Text(key ?? string.Empty, "keys", allowEmpty: true)!).ToArray();
        var checkedMeasures = new Dictionary<string, long>(measures.Count, StringComparer.Ordinal);
        foreach (var (name, value) in measures)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) throw Invalid("A measure is named in lowercase letters, digits and underscores.");
            checkedMeasures[name] = value;
        }

        // Written again from the contract: what the machine sent beyond it is not kept.
        return new FleetStatisticsRow(row.Family!, row.Quarter, Text(row.Provider, "provider"), Text(row.Model, "model"), keys, checkedMeasures);
    }

    private static string? Text(string? value, string field, bool required = false, int maxLength = MaxTextLength, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required) throw Invalid($"{field} is required.");
            return allowEmpty ? string.Empty : null;
        }

        if (value.Length > maxLength || value.Any(char.IsControl)) throw Invalid($"{field} is at most {maxLength} characters, without control characters.");
        return value;
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid, message);

    private static CoordinatorException Query(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.StatisticsInvalid, message);

    /// <summary>The sums of a group, as they are read.</summary>
    private sealed class Sums
    {
        private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _costs = new(StringComparer.Ordinal);

        public long Tokens => Get("usage/input_tokens") + Get("usage/output_tokens");

        public void Add(StatisticsSumRecord sum)
        {
            if (sum.Family == FleetStatisticsFamilies.Cost)
            {
                var unit = sum.Unit.Length == 0 ? "usd" : sum.Unit;
                _costs[unit] = _costs.GetValueOrDefault(unit) + sum.Value;
                return;
            }

            var key = sum.Family + "/" + sum.Measure;
            _values[key] = _values.GetValueOrDefault(key) + sum.Value;
        }

        public StatisticsMeasuresDto ToDto() => new(Get("activity/active_ms"), Get("activity/runs_started"), Get("activity/runs_failed"), Get("usage/requests"),
            Get("usage/input_tokens"), Get("usage/output_tokens"), Get("usage/cache_read_tokens"), Get("usage/cache_write_tokens"), Get("usage/reasoning_tokens"),
            Get("tool/calls"), Get("tool/failures"), new SortedDictionary<string, long>(_costs, StringComparer.Ordinal));

        private long Get(string key) => _values.GetValueOrDefault(key);
    }
}
