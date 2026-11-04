using System.Collections.Frozen;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The statistics facts machines publish over their control connection (<c>doc/fleet/statistics.md</c>): the rows the
/// Statistics plugin keeps per session and quarter hour, published per session as one versioned set.
/// </summary>
public static class FleetStatisticsMethods
{
    /// <summary>The feature the server announces in its welcome when it keeps statistics.</summary>
    public const string Feature = "statistics";

    /// <summary>The sets of rows of the sessions of the machine, versioned per session (<see cref="FleetStatisticsPublish"/>).</summary>
    public const string Publish = "statistics.publish";

    /// <summary>The sessions whose sets the machine takes back: made Private, or their space unlinked (<see cref="FleetStatisticsWithdraw"/>).</summary>
    public const string Withdraw = "statistics.withdraw";

    /// <summary>The path of the queries of the REST API.</summary>
    public const string QueryPath = "/api/v1/statistics/query";
}

/// <summary>The families of statistics facts a machine publishes, and how their measures merge.</summary>
public static class FleetStatisticsFamilies
{
    /// <summary>Active time and runs; keys: <c>effort</c>.</summary>
    public const string Activity = "activity";

    /// <summary>Requests to models and their tokens; keys: <c>effort</c>, <c>agent_prompt</c>, <c>purpose</c>.</summary>
    public const string Usage = "usage";

    /// <summary>Estimated costs in millionths of their unit; keys: <c>unit</c>.</summary>
    public const string Cost = "cost";

    /// <summary>Tool calls; keys: <c>kind</c>, <c>tool</c> (a name, never an argument).</summary>
    public const string Tools = "tool";

    /// <summary>The families a machine publishes.</summary>
    public static FrozenSet<string> All { get; } = FrozenSet.Create(StringComparer.Ordinal, Activity, Usage, Cost, Tools);

    /// <summary>The measures that merge with the larger value (the fullest context, the longest call); the others add.</summary>
    public static FrozenSet<string> MaxMeasures { get; } = FrozenSet.Create(StringComparer.Ordinal, "context_fill_ppm_max", "duration_ms_max");

    /// <summary>Gets the start of a quarter hour, the time unit of the rows.</summary>
    /// <param name="quarter">The index of the quarter hour since the Unix epoch, in UTC.</param>
    /// <returns>Its start.</returns>
    public static DateTimeOffset QuarterStart(long quarter) => DateTimeOffset.UnixEpoch.AddMinutes(quarter * 15);

    /// <summary>Gets the quarter hour that holds a time.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The index of its quarter hour since the Unix epoch, in UTC.</returns>
    public static long QuarterOf(DateTimeOffset time) => (long)Math.Floor(time.ToUnixTimeMilliseconds() / (15d * 60 * 1000));
}

/// <summary>
/// One row of statistics facts: a family, a quarter hour, its dimension (provider, model and the other keys of the family)
/// and its measures, which are counts and sums. Names, never content.
/// </summary>
/// <param name="Family">The family (<see cref="FleetStatisticsFamilies"/>).</param>
/// <param name="Quarter">The quarter hour, as an index since the Unix epoch in UTC.</param>
/// <param name="Provider">The provider the facts were made with; null when unknown.</param>
/// <param name="Model">The model; null when unknown or not a key of the family.</param>
/// <param name="Keys">The other keys of the family, in its order; null for none.</param>
/// <param name="Measures">The measures by name, such as <c>input_tokens</c>.</param>
public sealed record FleetStatisticsRow(string Family, long Quarter, string? Provider, string? Model, IReadOnlyList<string>? Keys, IReadOnlyDictionary<string, long> Measures);

/// <summary>
/// The rows of one session as the machine has them now, with the version of the set: the server keeps the set of the
/// highest version per machine and session and replaces every row of it, so a replay changes nothing and a row that
/// disappeared goes away. An empty set takes back the rows held.
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="Epoch">Its ownership epoch on the machine.</param>
/// <param name="Version">The version of the set, from 1.</param>
/// <param name="ProjectKey">The blind index of its project, which only the person's devices can name; null for none.</param>
/// <param name="Rows">The rows.</param>
/// <param name="SpaceKey">The opaque id of its space at the server, which budgets of a space count by; null when the machine has none yet.</param>
public sealed record FleetStatisticsSet(string SessionId, long Epoch, long Version, string? ProjectKey, IReadOnlyList<FleetStatisticsRow> Rows, string? SpaceKey = null);

/// <summary>The parameters of <c>statistics.publish</c>.</summary>
/// <param name="Sequence">The sequence of the batch, from 1.</param>
/// <param name="Generation">
/// The generation of the publications of the machine: a machine that lost what it published (its outbox) starts a new one,
/// and the server replaces everything the machine published before.
/// </param>
/// <param name="Sets">The sets.</param>
public sealed record FleetStatisticsPublish(long Sequence, string Generation, IReadOnlyList<FleetStatisticsSet> Sets);

/// <summary>The answer of <c>statistics.publish</c>.</summary>
/// <param name="Ack">The highest sequence the server holds.</param>
/// <param name="Applied">The sets that replaced what the server held.</param>
/// <param name="Unchanged">The sets the server held already, or newer.</param>
/// <param name="Stale">The sessions whose set has an older epoch than the one the server holds.</param>
public sealed record FleetStatisticsPublishResult(long Ack, int Applied, int Unchanged, IReadOnlyList<string> Stale);

/// <summary>The parameters of <c>statistics.withdraw</c>.</summary>
/// <param name="SessionIds">The sessions.</param>
public sealed record FleetStatisticsWithdraw(IReadOnlyList<string> SessionIds);

/// <summary>The answer of <c>statistics.withdraw</c>.</summary>
/// <param name="SessionIds">The sessions whose sets were deleted.</param>
public sealed record FleetStatisticsWithdrawResult(IReadOnlyList<string> SessionIds);

/// <summary>The JSON of the statistics contracts, source-generated.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetStatisticsSet))]
[JsonSerializable(typeof(FleetStatisticsPublish))]
[JsonSerializable(typeof(FleetStatisticsPublishResult))]
[JsonSerializable(typeof(FleetStatisticsWithdraw))]
[JsonSerializable(typeof(FleetStatisticsWithdrawResult))]
[JsonSerializable(typeof(StatisticsQueryRequest))]
[JsonSerializable(typeof(StatisticsQueryResponse))]
public sealed partial class FleetStatisticsJsonContext : JsonSerializerContext;
