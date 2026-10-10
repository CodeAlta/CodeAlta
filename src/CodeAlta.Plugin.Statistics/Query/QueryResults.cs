namespace CodeAlta.Plugin.Statistics.Query;

/// <summary>How much of the history the numbers of a query rest on.</summary>
/// <param name="Complete">Whether the numbers are complete for the whole period. False while the history is read, or when it was not read that far back: the page hatches the part before <paramref name="CompleteFrom"/>.</param>
/// <param name="HistoryState">Where the reading of the history stands: <c>needs-choice</c>, <c>reading</c>, <c>paused</c>, <c>stopped</c> or <c>done</c>.</param>
/// <param name="CompleteFrom">The first local day, as <c>yyyy-MM-dd</c>, from which the numbers are complete; null when everything is read.</param>
public sealed record StatisticsCoverage(bool Complete, string HistoryState, string? CompleteFrom);

/// <summary>What a query echoes of its request, and what the numbers rest on. Every result starts with it.</summary>
/// <param name="Period">The period as it was asked.</param>
/// <param name="From">The first local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="Frequency">The frequency that was used: <c>hour</c>, <c>day</c>, <c>week</c>, <c>month</c> or <c>year</c> (never <c>auto</c>).</param>
/// <param name="TimeZone">The time zone of the days.</param>
/// <param name="CompareFrom">The first day of the period it is compared with.</param>
/// <param name="CompareTo">The last day of the period it is compared with.</param>
/// <param name="Coverage">How much of the history the numbers rest on.</param>
/// <param name="IgnoredFilters">The filters of the request that this result could not apply.</param>
/// <param name="Notes">What to know to read the numbers: <c>space-membership-is-current</c> when a space filter is used, for instance.</param>
public sealed record QueryHeader(
    string Period,
    string From,
    string To,
    string Frequency,
    string TimeZone,
    string? CompareFrom,
    string? CompareTo,
    StatisticsCoverage Coverage,
    IReadOnlyList<string> IgnoredFilters,
    IReadOnlyList<string> Notes);

/// <summary>One bucket of a series.</summary>
/// <param name="Index">The position of the bucket.</param>
/// <param name="Start">The local start of the bucket: <c>yyyy-MM-ddTHH:mm</c>.</param>
/// <param name="Label">A short label: the hour, the day, the first day of the week, the month or the year.</param>
public sealed record BucketInfo(int Index, string Start, string Label);

/// <summary>One line of a chart: a value for each bucket.</summary>
/// <param name="Key">The key of the group the line is for; empty for the only line of a series without a group.</param>
/// <param name="Label">The name of the group.</param>
/// <param name="Values">The values, one for each bucket.</param>
/// <param name="Previous">The values of the compared period, one for each bucket; null without a comparison.</param>
/// <param name="Total">The sum of the values.</param>
/// <param name="PreviousTotal">The sum of the values of the compared period.</param>
public sealed record SeriesLine(string Key, string Label, IReadOnlyList<double> Values, IReadOnlyList<double>? Previous, double Total, double? PreviousTotal);

/// <summary>One metric over time, in buckets, with an optional group.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Metric">The metric.</param>
/// <param name="Unit">The unit of the values: <c>count</c>, <c>ms</c>, <c>tokens</c>, <c>bytes</c>, <c>lines</c>, or the unit of a cost.</param>
/// <param name="Group">The group the lines are for; null for one line.</param>
/// <param name="Buckets">The buckets.</param>
/// <param name="Series">The lines, the largest first.</param>
public sealed record SeriesResult(QueryHeader Query, string Metric, string Unit, string? Group, IReadOnlyList<BucketInfo> Buckets, IReadOnlyList<SeriesLine> Series);

/// <summary>One number of the summary, with its change and its line.</summary>
/// <param name="Id">The metric.</param>
/// <param name="Unit">The unit.</param>
/// <param name="Value">The value over the period.</param>
/// <param name="Previous">The value over the compared period.</param>
/// <param name="Change">The change against the compared period, as a ratio (0.25 is 25% more); null when there is nothing to compare.</param>
/// <param name="Spark">The value of each bucket.</param>
public sealed record SummaryTile(string Id, string Unit, double Value, double? Previous, double? Change, IReadOnlyList<double> Spark);

/// <summary>The tiles of the Overview: the numbers of a period at a glance.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Buckets">The buckets the lines of the tiles are cut into.</param>
/// <param name="Tiles">The tiles.</param>
/// <param name="Costs">The costs, one for each unit: they are never added up.</param>
public sealed record SummaryResult(QueryHeader Query, IReadOnlyList<BucketInfo> Buckets, IReadOnlyList<SummaryTile> Tiles, IReadOnlyList<SummaryTile> Costs);

/// <summary>One row of a ranking.</summary>
/// <param name="Key">The key: the name of a tool, the model, the project reference or the session.</param>
/// <param name="Label">The name to show.</param>
/// <param name="Detail">A second line: the provider of a model, the kind of a tool, the project of a session.</param>
/// <param name="Value">The value the ranking is by.</param>
/// <param name="Share">The share of the value in the total of the ranking, from 0 to 1.</param>
/// <param name="Tokens">The input and output tokens.</param>
/// <param name="TimeMs">The time in milliseconds: active time, or the time of the tool calls.</param>
/// <param name="Calls">The tool calls.</param>
/// <param name="Requests">The requests to models.</param>
/// <param name="Failures">The failed tool calls, for tools.</param>
/// <param name="Spark">The value over time.</param>
public sealed record RankedRow(
    string Key,
    string Label,
    string? Detail,
    double Value,
    double Share,
    double Tokens,
    double TimeMs,
    double Calls,
    double Requests,
    double? Failures,
    IReadOnlyList<double> Spark);

/// <summary>A ranking: the tools, the models, the projects or the sessions of a period.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Kind">What is ranked: <c>tools</c>, <c>models</c>, <c>projects</c> or <c>sessions</c>.</param>
/// <param name="By">The measure: <c>tokens</c>, <c>time</c> or <c>calls</c>.</param>
/// <param name="Unit">The unit of the value.</param>
/// <param name="Buckets">The buckets of the lines.</param>
/// <param name="Rows">The rows, the largest first.</param>
/// <param name="TotalRows">The number of rows before the limit.</param>
/// <param name="Truncated">Whether the limit cut rows.</param>
public sealed record TopResult(QueryHeader Query, string Kind, string By, string Unit, IReadOnlyList<BucketInfo> Buckets, IReadOnlyList<RankedRow> Rows, int TotalRows, bool Truncated);

/// <summary>A step of a distribution.</summary>
/// <param name="Lower">The smallest value of the step.</param>
/// <param name="Upper">The first value above the step; null for the last step.</param>
/// <param name="Count">The values in the step.</param>
public sealed record DistributionStep(long Lower, long? Upper, long Count);

/// <summary>A distribution in fixed steps, with its percentiles, which are exact within a step (about 19%).</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Measure">The measure.</param>
/// <param name="Subject">What the measure is of: a tool or a model; empty for none.</param>
/// <param name="Unit">The unit of the values.</param>
/// <param name="Count">The number of values.</param>
/// <param name="P50">The median.</param>
/// <param name="P90">The 90th percentile.</param>
/// <param name="Steps">The steps that hold values, in order.</param>
public sealed record DistributionResult(QueryHeader Query, string Measure, string? Subject, string Unit, long Count, double? P50, double? P90, IReadOnlyList<DistributionStep> Steps);

/// <summary>One day of the calendar.</summary>
/// <param name="Date">The local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="ActiveMs">The active time.</param>
/// <param name="Runs">The runs started.</param>
/// <param name="Prompts">The prompts you sent.</param>
public sealed record CalendarDay(string Date, double ActiveMs, long Runs, long Prompts);

/// <summary>The days of a period with their activity, for a heat map of a year.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Days">The days that have activity, in order.</param>
/// <param name="MaxActiveMs">The largest active time of a day.</param>
public sealed record CalendarResult(QueryHeader Query, IReadOnlyList<CalendarDay> Days, double MaxActiveMs);

/// <summary>The week by hour heat map: when in the week the work happens.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Weekdays">The names of the seven days, from the first day of the week.</param>
/// <param name="ActiveMs">The active time: seven rows (the days) of 24 values (the hours of the day).</param>
/// <param name="Runs">The runs started, in the same shape.</param>
public sealed record WeekHourResult(QueryHeader Query, IReadOnlyList<string> Weekdays, IReadOnlyList<IReadOnlyList<double>> ActiveMs, IReadOnlyList<IReadOnlyList<double>> Runs);

/// <summary>A cost in one unit.</summary>
/// <param name="Unit">The unit: <c>usd</c> or <c>AI credits</c>.</param>
/// <param name="Total">The cost.</param>
public sealed record CostAmount(string Unit, double Total);

/// <summary>A name and how many times the facts counted it.</summary>
/// <param name="Name">The name: a program, a command, an extension, a skill.</param>
/// <param name="Count">The count.</param>
/// <param name="Share">The share of the total of the list, from 0 to 1.</param>
public sealed record NameCount(string Name, long Count, double Share);

/// <summary>A list of counted names, ranked: the programs of shell commands, the commands of <c>alta</c>, the extensions of the files that were changed.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="List">The list.</param>
/// <param name="Rows">The names, the most counted first.</param>
/// <param name="Total">The sum of the counts of the list.</param>
/// <param name="TotalRows">The number of names before the limit.</param>
/// <param name="Truncated">Whether the limit cut rows.</param>
public sealed record DetailsResult(QueryHeader Query, string List, IReadOnlyList<NameCount> Rows, long Total, int TotalRows, bool Truncated);

/// <summary>One run, with what its prompt brought back.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="RunId">The run.</param>
/// <param name="Start">When it started, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</param>
/// <param name="DurationMs">How long it lasted.</param>
/// <param name="Outcome">How it ended: <c>running</c>, <c>completed</c>, <c>failed</c> or <c>interrupted</c>.</param>
/// <param name="Origin">Who sent the prompt that started it: <c>you</c>, <c>agent</c>, <c>reminder</c>, <c>automation</c> or <c>other</c>.</param>
/// <param name="PromptKind">How the prompt reached the session: <c>newturn</c>, <c>steer</c>, <c>queued</c> or <c>answer</c>.</param>
/// <param name="PromptChars">The characters of the prompt.</param>
/// <param name="PromptWords">The words of the prompt.</param>
/// <param name="Requests">The requests to models.</param>
/// <param name="ToolCalls">The tool calls.</param>
/// <param name="ToolFailures">The tool calls that failed.</param>
/// <param name="InputTokens">The input tokens.</param>
/// <param name="OutputTokens">The output tokens.</param>
/// <param name="AnswerChars">The characters the assistant wrote.</param>
/// <param name="AnswerWords">The words the assistant wrote.</param>
/// <param name="Provider">The provider at the start of the run.</param>
/// <param name="Model">The model at the start of the run.</param>
/// <param name="Effort">The reasoning effort at the start of the run.</param>
public sealed record RunSample(
    string SessionId,
    string RunId,
    string Start,
    double DurationMs,
    string Outcome,
    string Origin,
    string PromptKind,
    long PromptChars,
    long PromptWords,
    long Requests,
    long ToolCalls,
    long ToolFailures,
    long InputTokens,
    long OutputTokens,
    long AnswerChars,
    long AnswerWords,
    string Provider,
    string Model,
    string Effort);

/// <summary>The runs of a period.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Sort">The measure the runs are sorted on.</param>
/// <param name="Runs">The runs.</param>
/// <param name="TotalRows">The number of runs before the limit.</param>
/// <param name="Truncated">Whether the limit cut rows.</param>
public sealed record RunsResult(QueryHeader Query, string Sort, IReadOnlyList<RunSample> Runs, int TotalRows, bool Truncated);

/// <summary>One session of the table of sessions.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Title">Its title.</param>
/// <param name="Project">The reference of its project.</param>
/// <param name="ProjectName">The name of its project.</param>
/// <param name="Provider">The provider it was created with.</param>
/// <param name="Model">The model it used the most.</param>
/// <param name="ParentSessionId">The session that created it, for a sub-agent.</param>
/// <param name="Runs">The runs started in the period.</param>
/// <param name="ActiveMs">The active time in the period.</param>
/// <param name="Tokens">The input and output tokens in the period.</param>
/// <param name="ToolCalls">The tool calls in the period.</param>
/// <param name="Costs">The costs in the period, by unit.</param>
/// <param name="SubAgents">The sessions it created, in all.</param>
/// <param name="LastActivity">The last record, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</param>
/// <param name="Deleted">Whether the session was deleted since: its numbers are kept.</param>
public sealed record SessionEntry(
    string SessionId,
    string? Title,
    string? Project,
    string? ProjectName,
    string? Provider,
    string? Model,
    string? ParentSessionId,
    long Runs,
    double ActiveMs,
    double Tokens,
    double ToolCalls,
    IReadOnlyList<CostAmount> Costs,
    int SubAgents,
    string? LastActivity,
    bool Deleted);

/// <summary>The sessions that were active in a period.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Sort">The measure the rows are sorted on.</param>
/// <param name="Rows">The rows.</param>
/// <param name="TotalRows">The number of sessions before the limit.</param>
/// <param name="Truncated">Whether the limit cut rows.</param>
public sealed record SessionsResult(QueryHeader Query, string Sort, IReadOnlyList<SessionEntry> Rows, int TotalRows, bool Truncated);

/// <summary>One tool of the table of tools.</summary>
/// <param name="Kind">The kind of the tool.</param>
/// <param name="Tool">The bucket of the tool.</param>
/// <param name="Calls">The calls.</param>
/// <param name="Failures">The calls that failed.</param>
/// <param name="FailureRate">The failures over the calls.</param>
/// <param name="TimeMs">The total time of the calls.</param>
/// <param name="P50Ms">The median duration.</param>
/// <param name="P90Ms">The 90th percentile of the duration.</param>
/// <param name="MaxMs">The longest call.</param>
/// <param name="BytesIn">The bytes of the arguments.</param>
/// <param name="BytesOut">The bytes of the results.</param>
/// <param name="Spark">The calls over time.</param>
public sealed record ToolRow(string Kind, string Tool, long Calls, long Failures, double FailureRate, double TimeMs, double? P50Ms, double? P90Ms, double MaxMs, long BytesIn, long BytesOut, IReadOnlyList<double> Spark);

/// <summary>The tools of a period.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Buckets">The buckets of the lines.</param>
/// <param name="Rows">The tools, the most called first.</param>
/// <param name="Kinds">The calls by kind of tool.</param>
/// <param name="TotalRows">The number of tools before the limit.</param>
/// <param name="Truncated">Whether the limit cut rows.</param>
public sealed record ToolsResult(QueryHeader Query, IReadOnlyList<BucketInfo> Buckets, IReadOnlyList<ToolRow> Rows, IReadOnlyList<KeyValuePair<string, long>> Kinds, int TotalRows, bool Truncated);

/// <summary>One model of the table of models.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Requests">The requests.</param>
/// <param name="InputTokens">All the input tokens.</param>
/// <param name="FreshInputTokens">The input that was not read from the cache and not written to it.</param>
/// <param name="CacheReadTokens">The input read from the cache.</param>
/// <param name="CacheWriteTokens">The input written to the cache.</param>
/// <param name="OutputTokens">The output tokens, the reasoning ones included.</param>
/// <param name="ReasoningTokens">The reasoning tokens.</param>
/// <param name="CacheShare">The input read from the cache over all the input.</param>
/// <param name="ActiveMs">The active time given to the model.</param>
/// <param name="AverageContextFill">The average fill of the context window, from 0 to 1; null without a sample.</param>
/// <param name="HighestContextFill">The highest fill of the context window.</param>
/// <param name="Costs">The costs, by unit.</param>
/// <param name="Spark">The tokens over time.</param>
public sealed record ModelRow(
    string Provider,
    string Model,
    long Requests,
    long InputTokens,
    long FreshInputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    long ReasoningTokens,
    double CacheShare,
    double ActiveMs,
    double? AverageContextFill,
    double HighestContextFill,
    IReadOnlyList<CostAmount> Costs,
    IReadOnlyList<double> Spark);

/// <summary>A model with a reasoning effort.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Effort">The reasoning effort; empty for none.</param>
/// <param name="Requests">The requests.</param>
/// <param name="Tokens">The input and output tokens.</param>
/// <param name="ReasoningShare">The reasoning tokens over the output tokens.</param>
public sealed record EffortRow(string Provider, string Model, string Effort, long Requests, long Tokens, double ReasoningShare);

/// <summary>The models of a period.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Buckets">The buckets of the lines.</param>
/// <param name="Rows">The models, the most used first.</param>
/// <param name="Efforts">The models by reasoning effort.</param>
/// <param name="TotalRows">The number of models before the limit.</param>
/// <param name="Truncated">Whether the limit cut rows.</param>
public sealed record ModelsResult(QueryHeader Query, IReadOnlyList<BucketInfo> Buckets, IReadOnlyList<ModelRow> Rows, IReadOnlyList<EffortRow> Efforts, int TotalRows, bool Truncated);

/// <summary>One project of the table of projects.</summary>
/// <param name="Project">The reference of the project.</param>
/// <param name="Name">The name of the project.</param>
/// <param name="Sessions">The sessions that were active.</param>
/// <param name="Runs">The runs started.</param>
/// <param name="ActiveMs">The active time.</param>
/// <param name="Tokens">The input and output tokens.</param>
/// <param name="ToolCalls">The tool calls.</param>
/// <param name="Costs">The costs, by unit.</param>
/// <param name="Spark">The active time over time.</param>
public sealed record ProjectRow(string Project, string Name, int Sessions, long Runs, double ActiveMs, double Tokens, double ToolCalls, IReadOnlyList<CostAmount> Costs, IReadOnlyList<double> Spark);

/// <summary>The projects of a period.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Buckets">The buckets of the lines.</param>
/// <param name="Rows">The projects, the most active first.</param>
/// <param name="TotalRows">The number of projects before the limit.</param>
/// <param name="Truncated">Whether the limit cut rows.</param>
public sealed record ProjectsResult(QueryHeader Query, IReadOnlyList<BucketInfo> Buckets, IReadOnlyList<ProjectRow> Rows, int TotalRows, bool Truncated);

/// <summary>A record: the largest value of a measure and where it came from.</summary>
/// <param name="Measure">The measure: <c>longestRun</c>, <c>mostToolCallsInRun</c>, <c>longestTool</c>, <c>highestContextFill</c>, <c>largestRequestInput</c>, <c>busiestDay</c>, <c>longestStreak</c> or <c>largestPrompt</c>.</param>
/// <param name="Subject">What it is of: a tool or a model; empty for none.</param>
/// <param name="Value">The value.</param>
/// <param name="Unit">The unit.</param>
/// <param name="SessionId">The session it comes from.</param>
/// <param name="RunId">The run it comes from.</param>
/// <param name="At">When, <c>yyyy-MM-ddTHH:mm:ssZ</c> or a local day.</param>
public sealed record RecordEntry(string Measure, string Subject, double Value, string Unit, string? SessionId, string? RunId, string? At);

/// <summary>The records of a period.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Records">The records.</param>
public sealed record RecordsResult(QueryHeader Query, IReadOnlyList<RecordEntry> Records);

/// <summary>How the work goes: errors, interrupted runs, compactions, how full the context gets.</summary>
/// <param name="Query">What the query echoes.</param>
/// <param name="Buckets">The buckets of the lines.</param>
/// <param name="Errors">The errors over time.</param>
/// <param name="InterruptedRuns">The runs found without an end over time.</param>
/// <param name="Runs">The runs started.</param>
/// <param name="ErrorRate">The errors over the runs.</param>
/// <param name="FailedTools">The tools with failed calls, the most failures first.</param>
/// <param name="CompactionsByTrigger">The compactions by trigger.</param>
/// <param name="Compactions">The compactions over time.</param>
/// <param name="TokensBeforeCompaction">The tokens the compactions started from.</param>
/// <param name="TokensAfterCompaction">The tokens the compactions ended with.</param>
/// <param name="ContextByModel">The fullest and the average fill of the context window, by model.</param>
public sealed record HealthResult(
    QueryHeader Query,
    IReadOnlyList<BucketInfo> Buckets,
    IReadOnlyList<double> Errors,
    IReadOnlyList<double> InterruptedRuns,
    long Runs,
    double ErrorRate,
    IReadOnlyList<ToolRow> FailedTools,
    IReadOnlyList<KeyValuePair<string, long>> CompactionsByTrigger,
    IReadOnlyList<double> Compactions,
    long TokensBeforeCompaction,
    long TokensAfterCompaction,
    IReadOnlyList<ContextFillRow> ContextByModel);

/// <summary>The fill of the context window of a model.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Average">The average fill, from 0 to 1.</param>
/// <param name="Highest">The highest fill.</param>
/// <param name="Samples">The requests that reported it.</param>
public sealed record ContextFillRow(string Provider, string Model, double? Average, double Highest, long Samples);

/// <summary>One run of a session.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Start">When it started, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</param>
/// <param name="DurationMs">How long it lasted.</param>
/// <param name="Outcome">How it ended: <c>running</c>, <c>completed</c>, <c>failed</c> or <c>interrupted</c>.</param>
/// <param name="Sender">Who sent the prompt that started it.</param>
/// <param name="Requests">Its requests.</param>
/// <param name="ToolCalls">Its tool calls.</param>
/// <param name="InputTokens">Its input tokens.</param>
/// <param name="OutputTokens">Its output tokens.</param>
/// <param name="Model">The model at its start.</param>
public sealed record RunEntry(string RunId, string Start, double DurationMs, string Outcome, string Sender, long Requests, long ToolCalls, long InputTokens, long OutputTokens, string Model);

/// <summary>The numbers of one session, with its sub-agents when asked.</summary>
/// <param name="Query">What the query echoes: the period is the whole history.</param>
/// <param name="Session">The session.</param>
/// <param name="Children">The sub-agent sessions, when they were asked for.</param>
/// <param name="Totals">The totals of the session, and of its sub-agents when asked.</param>
/// <param name="Runs">The latest runs of the session, newest first, at most fifty.</param>
/// <param name="Models">The models the session used.</param>
/// <param name="Tools">The tools it called, the most called first, at most ten.</param>
public sealed record SessionDetailResult(
    QueryHeader Query,
    SessionEntry Session,
    IReadOnlyList<SessionEntry> Children,
    IReadOnlyList<SummaryTile> Totals,
    IReadOnlyList<RunEntry> Runs,
    IReadOnlyList<ModelRow> Models,
    IReadOnlyList<ToolRow> Tools);
