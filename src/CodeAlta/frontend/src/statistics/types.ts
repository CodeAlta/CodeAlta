// What the Statistics plugin returns, as JSON: the same shapes as `src/CodeAlta.Plugin.Statistics/Query/QueryResults.cs`,
// `StatisticsRequest.cs` and `History/StatisticsStatus.cs`, written by `StatisticsJson` (camelCase names, enums as text,
// null properties left out). `doc/statistics.md` documents every property.

/** The size of the buckets a result is cut into; a result never says `auto`. */
export type Frequency = "hour" | "day" | "week" | "month" | "year";
/** The size of the buckets a request asks for. */
export type RequestFrequency = Frequency | "auto";
/** What a period is compared with. */
export type Comparison = "none" | "previousPeriod" | "samePeriodLastYear";
/** Who started the work. */
export type Origin = "you" | "agent" | "automation" | "reminder";
/** The kinds of tool. */
export type ToolKind = "files" | "search" | "shell" | "web" | "alta" | "mcp" | "skill" | "other";
/** The days of the week, as the host writes them. */
export type WeekDayName = "Sunday" | "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday";

/** The filters of a request: each is one value; absent means no filter. */
export type StatisticsFilter = Readonly<{
  space?: string;
  project?: string;
  provider?: string;
  model?: string;
  effort?: string;
  origin?: Origin;
  toolKind?: ToolKind;
}>;

/** What a page asks for: a period, a frequency, filters and a comparison. */
export type StatisticsRequest = Readonly<{
  /** `today`, `yesterday`, `7d` (any `Nd`), `week`, `month`, `last-month`, `year`, `all` or `yyyy-MM-dd..yyyy-MM-dd`. */
  period: string;
  frequency?: RequestFrequency;
  comparison?: Comparison;
  filter?: StatisticsFilter;
  weekStart?: WeekDayName;
  limit?: number;
}>;

/** How much of the history the numbers of a query rest on. */
export type Coverage = Readonly<{
  complete: boolean;
  /** `needs-choice`, `reading`, `paused`, `stopped` or `done`. */
  historyState: string;
  /** The first local day (`yyyy-MM-dd`) from which the numbers are complete. */
  completeFrom?: string;
}>;

/** What a query echoes of its request; every result starts with it. */
export type QueryHeader = Readonly<{
  period: string;
  from: string;
  to: string;
  frequency: Frequency;
  timeZone: string;
  compareFrom?: string;
  compareTo?: string;
  coverage: Coverage;
  ignoredFilters: readonly string[];
  notes: readonly string[];
}>;

/** One bucket of a series. */
export type BucketInfo = Readonly<{ index: number; start: string; label: string }>;

/** One line of a chart. */
export type SeriesLine = Readonly<{
  key: string;
  label: string;
  values: readonly number[];
  previous?: readonly number[];
  total: number;
  previousTotal?: number;
}>;

/** One metric over time. */
export type SeriesResult = Readonly<{
  query: QueryHeader;
  metric: string;
  /** `count`, `ms`, `tokens`, `bytes`, `lines`, or the unit of a cost. */
  unit: string;
  group?: string;
  buckets: readonly BucketInfo[];
  series: readonly SeriesLine[];
}>;

/** One number of the summary. */
export type SummaryTile = Readonly<{ id: string; unit: string; value: number; previous?: number; change?: number; spark: readonly number[] }>;

/** The tiles of the Overview. */
export type SummaryResult = Readonly<{ query: QueryHeader; buckets: readonly BucketInfo[]; tiles: readonly SummaryTile[]; costs: readonly SummaryTile[] }>;

/** One row of a ranking. */
export type RankedRow = Readonly<{
  key: string;
  label: string;
  detail?: string;
  value: number;
  share: number;
  tokens: number;
  timeMs: number;
  calls: number;
  requests: number;
  failures?: number;
  spark: readonly number[];
}>;

/** A ranking: tools, models, projects or sessions. */
export type TopResult = Readonly<{
  query: QueryHeader;
  kind: string;
  by: string;
  unit: string;
  buckets: readonly BucketInfo[];
  rows: readonly RankedRow[];
  totalRows: number;
  truncated: boolean;
}>;

/** A step of a distribution: `upper` is absent for the last step. */
export type DistributionStep = Readonly<{ lower: number; upper?: number; count: number }>;

/** A distribution in fixed steps with its percentiles. */
export type DistributionResult = Readonly<{
  query: QueryHeader;
  measure: string;
  subject?: string;
  unit: string;
  count: number;
  p50?: number;
  p90?: number;
  steps: readonly DistributionStep[];
}>;

/** One day of the calendar. */
export type CalendarDay = Readonly<{ date: string; activeMs: number; runs: number; prompts: number }>;
/** A year of days. */
export type CalendarResult = Readonly<{ query: QueryHeader; days: readonly CalendarDay[]; maxActiveMs: number }>;
/** The day of the week by hour. */
export type WeekHourResult = Readonly<{ query: QueryHeader; weekdays: readonly string[]; activeMs: readonly (readonly number[])[]; runs: readonly (readonly number[])[] }>;

/** A cost in one unit: `usd` or `AI credits`. */
export type CostAmount = Readonly<{ unit: string; total: number }>;
/** A name and how many times the facts counted it. */
export type NameCount = Readonly<{ name: string; count: number; share: number }>;
/** A list of counted names. */
export type DetailsResult = Readonly<{ query: QueryHeader; list: string; rows: readonly NameCount[]; total: number; totalRows: number; truncated: boolean }>;

/** One run with what its prompt brought back. */
export type RunSample = Readonly<{
  sessionId: string;
  runId: string;
  start: string;
  durationMs: number;
  outcome: string;
  origin: string;
  promptKind: string;
  promptChars: number;
  promptWords: number;
  requests: number;
  toolCalls: number;
  toolFailures: number;
  inputTokens: number;
  outputTokens: number;
  answerChars: number;
  answerWords: number;
  provider: string;
  model: string;
  effort: string;
}>;
/** The runs of a period. */
export type RunsResult = Readonly<{ query: QueryHeader; sort: string; runs: readonly RunSample[]; totalRows: number; truncated: boolean }>;

/** One session of the table of sessions. */
export type SessionEntry = Readonly<{
  sessionId: string;
  title?: string;
  project?: string;
  projectName?: string;
  provider?: string;
  model?: string;
  parentSessionId?: string;
  runs: number;
  activeMs: number;
  tokens: number;
  toolCalls: number;
  costs: readonly CostAmount[];
  subAgents: number;
  lastActivity?: string;
  deleted: boolean;
}>;
/** The sessions active in a period. */
export type SessionsResult = Readonly<{ query: QueryHeader; sort: string; rows: readonly SessionEntry[]; totalRows: number; truncated: boolean }>;

/** One tool of the table of tools. */
export type ToolRow = Readonly<{
  kind: string;
  tool: string;
  calls: number;
  failures: number;
  failureRate: number;
  timeMs: number;
  p50Ms?: number;
  p90Ms?: number;
  maxMs: number;
  bytesIn: number;
  bytesOut: number;
  spark: readonly number[];
}>;
/** `KeyValuePair<string, long>` as System.Text.Json writes it. */
export type KeyCount = Readonly<{ key: string; value: number }>;
/** The tools of a period. */
export type ToolsResult = Readonly<{ query: QueryHeader; buckets: readonly BucketInfo[]; rows: readonly ToolRow[]; kinds: readonly KeyCount[]; totalRows: number; truncated: boolean }>;

/** One model of the table of models. */
export type ModelRow = Readonly<{
  provider: string;
  model: string;
  requests: number;
  inputTokens: number;
  freshInputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  outputTokens: number;
  reasoningTokens: number;
  cacheShare: number;
  activeMs: number;
  averageContextFill?: number;
  highestContextFill: number;
  costs: readonly CostAmount[];
  spark: readonly number[];
}>;
/** A model with a reasoning effort. */
export type EffortRow = Readonly<{ provider: string; model: string; effort: string; requests: number; tokens: number; reasoningShare: number }>;
/** The models of a period. */
export type ModelsResult = Readonly<{ query: QueryHeader; buckets: readonly BucketInfo[]; rows: readonly ModelRow[]; efforts: readonly EffortRow[]; totalRows: number; truncated: boolean }>;

/** One project of the table of projects. */
export type ProjectRow = Readonly<{ project: string; name: string; sessions: number; runs: number; activeMs: number; tokens: number; toolCalls: number; costs: readonly CostAmount[]; spark: readonly number[] }>;
/** The projects of a period. */
export type ProjectsResult = Readonly<{ query: QueryHeader; buckets: readonly BucketInfo[]; rows: readonly ProjectRow[]; totalRows: number; truncated: boolean }>;

/** A record: the largest value of a measure and where it came from. */
export type RecordEntry = Readonly<{ measure: string; subject: string; value: number; unit: string; sessionId?: string; runId?: string; at?: string }>;
/** The records of a period. */
export type RecordsResult = Readonly<{ query: QueryHeader; records: readonly RecordEntry[] }>;

/** The fill of the context window of a model. */
export type ContextFillRow = Readonly<{ provider: string; model: string; average?: number; highest: number; samples: number }>;
/** Errors, interrupted runs, compactions and how full the context gets. */
export type HealthResult = Readonly<{
  query: QueryHeader;
  buckets: readonly BucketInfo[];
  errors: readonly number[];
  interruptedRuns: readonly number[];
  runs: number;
  errorRate: number;
  failedTools: readonly ToolRow[];
  compactionsByTrigger: readonly KeyCount[];
  compactions: readonly number[];
  tokensBeforeCompaction: number;
  tokensAfterCompaction: number;
  contextByModel: readonly ContextFillRow[];
}>;

/** One run of a session. */
export type RunEntry = Readonly<{ runId: string; start: string; durationMs: number; outcome: string; sender: string; requests: number; toolCalls: number; inputTokens: number; outputTokens: number; model: string }>;
/** The numbers of one session. */
export type SessionDetailResult = Readonly<{
  query: QueryHeader;
  session: SessionEntry;
  children: readonly SessionEntry[];
  totals: readonly SummaryTile[];
  runs: readonly RunEntry[];
  models: readonly ModelRow[];
  tools: readonly ToolRow[];
}>;

/** Only the coverage of a period. */
export type CoverageResult = Coverage;

/** Where the reading of the history stands: `HistoryState`, as camelCase text. */
export type HistoryState = "starting" | "needsChoice" | "reading" | "paused" | "stoppedHere" | "done" | "failed";

/** A session the history could not read. */
export type SkippedSession = Readonly<{ sessionId: string; reason: string }>;

/** A snapshot of the reading of the history: what the bar of the page shows. */
export type StatisticsStatus = Readonly<{
  state: HistoryState;
  /** `first-read`, `extended`, `facts-improved` or `catch-up`. */
  reason?: string;
  /** `all`, `days:30` or `from-today`. */
  choice?: string;
  /** The first local day kept, `yyyymmdd`. */
  floorDay?: number;
  sessionsTotal: number;
  sessionsDone: number;
  bytesTotal: number;
  bytesDone: number;
  /** The oldest local day the reading has reached, `yyyymmdd`. */
  oldestDateReached?: number;
  /** The numbers are complete from this local day, `yyyymmdd`. */
  completeFromDay?: number;
  bytesPerSecond?: number;
  etaSeconds?: number;
  currentSessionId?: string;
  skippedCount: number;
  skipped: readonly SkippedSession[];
  pendingFlow: number;
  revision: number;
  error?: string;
  isComplete?: boolean;
}>;

/** What the user chooses to read of the history. */
export type HistoryChoice = Readonly<{ kind: "all" } | { kind: "days"; days: number } | { kind: "fromToday" }>;

/** What changed when a catch-up was saved: the first and last local day (`yyyymmdd`) and the sessions. */
export type StatisticsDataChange = Readonly<{ revision: number; fromDay: number; toDay: number; sessionIds: readonly string[] }>;
