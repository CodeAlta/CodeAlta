namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>The dimensions a fleet statistics query groups by.</summary>
public static class StatisticsGroupBy
{
    /// <summary>The machine that counted the facts.</summary>
    public const string Machine = "machine";

    /// <summary>The project key: a blind index, named by the person's devices.</summary>
    public const string Project = "project";

    /// <summary>The provider.</summary>
    public const string Provider = "provider";

    /// <summary>The model.</summary>
    public const string Model = "model";

    /// <summary>The local day.</summary>
    public const string Day = "day";

    /// <summary>The local month.</summary>
    public const string Month = "month";

    /// <summary>Gets every dimension.</summary>
    public static IReadOnlyList<string> All { get; } = [Machine, Project, Provider, Model, Day, Month];
}

/// <summary>The body of <c>POST /api/v1/statistics/query</c>: sums of the statistics facts machines published.</summary>
/// <param name="From">The first local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last local day, included, <c>yyyy-MM-dd</c>.</param>
/// <param name="TimeZone">The time zone of the days (an IANA or Windows id); null for UTC.</param>
/// <param name="GroupBy">The dimensions to group by (<see cref="StatisticsGroupBy"/>), at most three; null or empty for the totals only.</param>
/// <param name="Scope">Whose facts: <c>mine</c>, the machines of the calling device's person (the only scope for now).</param>
/// <param name="Limit">The most groups, the largest first by tokens; at most 500.</param>
public sealed record StatisticsQueryRequest(string From, string To, string? TimeZone = null, IReadOnlyList<string>? GroupBy = null, string? Scope = null, int? Limit = null);

/// <summary>The sums of a group of statistics facts. Costs are in millionths of their unit, by unit (<c>usd</c>, <c>credits</c>).</summary>
/// <param name="ActiveMs">The active time of the runs.</param>
/// <param name="Runs">The runs started.</param>
/// <param name="RunsFailed">The runs that failed.</param>
/// <param name="Requests">The requests to models.</param>
/// <param name="InputTokens">The input tokens.</param>
/// <param name="OutputTokens">The output tokens.</param>
/// <param name="CacheReadTokens">The input tokens read from a cache.</param>
/// <param name="CacheWriteTokens">The input tokens written to a cache.</param>
/// <param name="ReasoningTokens">The reasoning tokens.</param>
/// <param name="ToolCalls">The tool calls.</param>
/// <param name="ToolFailures">The tool calls that failed.</param>
/// <param name="CostMicro">The estimated costs by unit, in millionths of the unit.</param>
public sealed record StatisticsMeasuresDto(
    long ActiveMs,
    long Runs,
    long RunsFailed,
    long Requests,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    long ToolCalls,
    long ToolFailures,
    IReadOnlyDictionary<string, long> CostMicro);

/// <summary>One group of a fleet statistics query: the values of its dimensions (null for those not grouped by) and its sums.</summary>
/// <param name="Machine">The device id of the machine.</param>
/// <param name="Project">The project key; empty for the sessions without a project.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Period">The local day (<c>yyyy-MM-dd</c>) or month (<c>yyyy-MM</c>).</param>
/// <param name="Measures">The sums.</param>
public sealed record StatisticsGroupDto(string? Machine, string? Project, string? Provider, string? Model, string? Period, StatisticsMeasuresDto Measures);

/// <summary>A machine of the scope of a query and its last publication, so that a gap shows rather than hides.</summary>
/// <param name="Id">The device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its kind (<c>node</c>, <c>desktop</c>).</param>
/// <param name="LastPublishedAt">When it last published statistics; null when never.</param>
/// <param name="Sessions">The sessions whose facts it published.</param>
public sealed record StatisticsMachineDto(string Id, string Name, string Kind, DateTimeOffset? LastPublishedAt, long Sessions);

/// <summary>The answer of <c>POST /api/v1/statistics/query</c>.</summary>
/// <param name="Scope">The scope answered.</param>
/// <param name="From">The first local day.</param>
/// <param name="To">The last local day.</param>
/// <param name="TimeZone">The time zone of the days.</param>
/// <param name="GroupBy">The dimensions of the groups.</param>
/// <param name="Totals">The sums of the period.</param>
/// <param name="Groups">The groups, the largest first by tokens.</param>
/// <param name="Machines">The machines of the scope.</param>
/// <param name="Truncated">Whether groups were left out by the limit.</param>
public sealed record StatisticsQueryResponse(
    string Scope,
    string From,
    string To,
    string TimeZone,
    IReadOnlyList<string> GroupBy,
    StatisticsMeasuresDto Totals,
    IReadOnlyList<StatisticsGroupDto> Groups,
    IReadOnlyList<StatisticsMachineDto> Machines,
    bool Truncated);
