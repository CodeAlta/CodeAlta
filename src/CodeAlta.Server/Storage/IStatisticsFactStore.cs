using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Server.Storage;

/// <summary>The set of statistics rows of one session of a machine, as the machine published it.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Epoch">Its ownership epoch on the machine.</param>
/// <param name="Version">The version of the set.</param>
/// <param name="ProjectKey">The blind index of its project; null for none.</param>
/// <param name="Rows">The rows.</param>
public sealed record StatisticsSetRecord(string SessionId, long Epoch, long Version, string? ProjectKey, IReadOnlyList<FleetStatisticsRow> Rows);

/// <summary>What happened to a set of statistics rows.</summary>
public enum StatisticsSetOutcome
{
    /// <summary>The set replaced what the server held.</summary>
    Applied,

    /// <summary>The server held this version or a newer one.</summary>
    Unchanged,

    /// <summary>The set is of an older epoch than the one the server holds.</summary>
    Stale,
}

/// <summary>The result of a publication of statistics sets.</summary>
/// <param name="Ack">The highest sequence the server holds of the machine.</param>
/// <param name="Outcomes">What happened to each set.</param>
public sealed record StatisticsPublication(long Ack, IReadOnlyList<(string SessionId, StatisticsSetOutcome Outcome)> Outcomes);

/// <summary>A sum of statistics rows: a time range, its dimensions, the machines whose rows count.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="OwnerPersonId">The person whose machines count; null for every machine of the organization.</param>
/// <param name="Buckets">The time ranges of the answer, in quarter hours, each from its first quarter to the one after its last.</param>
/// <param name="ByMachine">Whether to group by machine.</param>
/// <param name="ByProject">Whether to group by project key.</param>
/// <param name="ByProvider">Whether to group by provider.</param>
/// <param name="ByModel">Whether to group by model.</param>
/// <param name="Measures">The measures summed, by family.</param>
public sealed record StatisticsSumQuery(
    string OrganizationId,
    string? OwnerPersonId,
    IReadOnlyList<(long From, long To)> Buckets,
    bool ByMachine,
    bool ByProject,
    bool ByProvider,
    bool ByModel,
    IReadOnlyList<(string Family, string Measure)> Measures);

/// <summary>One sum of a <see cref="StatisticsSumQuery"/>.</summary>
/// <param name="Bucket">The index of its time range.</param>
/// <param name="Machine">The machine, when grouped by it.</param>
/// <param name="Project">The project key (empty without a project), when grouped by it.</param>
/// <param name="Provider">The provider, when grouped by it.</param>
/// <param name="Model">The model, when grouped by it.</param>
/// <param name="Family">The family of the measure.</param>
/// <param name="Measure">The measure.</param>
/// <param name="Unit">For a cost, its unit; empty otherwise.</param>
/// <param name="Value">The sum.</param>
public sealed record StatisticsSumRecord(int Bucket, string? Machine, string? Project, string? Provider, string? Model, string Family, string Measure, string Unit, long Value);

/// <summary>A machine that published statistics.</summary>
/// <param name="DeviceId">Its device.</param>
/// <param name="PublishedAt">When it last published.</param>
/// <param name="Sessions">The sessions whose sets it published.</param>
public sealed record StatisticsPublisherRecord(string DeviceId, DateTimeOffset PublishedAt, long Sessions);

/// <summary>
/// The statistics facts machines publish (<c>doc/fleet/statistics.md</c>): sets of rows per machine and session, of the highest
/// version, replaced whole; sums of their measures by dimension and period.
/// </summary>
public interface IStatisticsFactStore
{
    /// <summary>
    /// Keeps the sets a machine publishes: each replaces the set held when its epoch is newer, or its epoch the same and its version
    /// higher. A generation other than the one held first deletes every set of the machine. Moves the statistics cursor of the device.
    /// </summary>
    /// <param name="deviceId">The machine.</param>
    /// <param name="organizationId">Its organization.</param>
    /// <param name="sequence">The sequence of the batch.</param>
    /// <param name="generation">The generation of the publications of the machine.</param>
    /// <param name="sets">The sets, checked by the caller.</param>
    /// <param name="now">The time of the publication.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cursor and the outcome of each set.</returns>
    ValueTask<StatisticsPublication> PublishStatisticsAsync(string deviceId, string organizationId, long sequence, string generation, IReadOnlyList<StatisticsSetRecord> sets,
        DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Deletes the sets of sessions a machine no longer publishes, audited in the same transaction.</summary>
    /// <param name="deviceId">The machine.</param>
    /// <param name="sessionIds">The sessions.</param>
    /// <param name="audit">The audit entry, written when a set was deleted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The sessions whose set was deleted.</returns>
    ValueTask<IReadOnlyList<string>> WithdrawStatisticsAsync(string deviceId, IReadOnlyList<string> sessionIds, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Sums the measures of the rows of a query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sums, by bucket, dimension and measure.</returns>
    ValueTask<IReadOnlyList<StatisticsSumRecord>> SumStatisticsAsync(StatisticsSumQuery query, CancellationToken cancellationToken = default);

    /// <summary>Lists the machines of an organization that published statistics.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The machines.</returns>
    ValueTask<IReadOnlyList<StatisticsPublisherRecord>> ListStatisticsPublishersAsync(string organizationId, CancellationToken cancellationToken = default);
}
