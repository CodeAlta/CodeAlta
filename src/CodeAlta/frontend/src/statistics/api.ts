import type {
  CalendarResult, DetailsResult, DistributionResult, HealthResult, HistoryChoice, ModelsResult, ProjectsResult, RecordsResult, RunsResult, SeriesResult,
  SessionDetailResult, SessionsResult, StatisticsDataChange, StatisticsRequest, StatisticsStatus, SummaryResult, ToolsResult, TopResult, WeekHourResult,
} from "./types";

/** What the canvas hears from the Statistics plugin without asking. */
export type StatisticsEvent =
  | Readonly<{ kind: "status"; status: StatisticsStatus }>
  | Readonly<{ kind: "data"; change: StatisticsDataChange }>;

/**
 * The questions and the controls of the Statistics plugin, as the canvas uses them: one method per question, the controls of
 * the history, and a subscription to what changes. Every result is exactly the JSON of `doc/statistics.md`.
 *
 * The canvas never imports a transport: the application binds this interface to the plugin (`alta.rpc`), a test binds it to a
 * recording fake, and `createFixtureApi` is a realistic one over generated data. A method rejects when the question is not
 * valid or the plugin is unavailable; it rejects with an `AbortError` when its signal is aborted, and the canvas ignores that.
 */
export interface StatisticsApi {
  /** The tiles of the Overview. */
  summary(request: StatisticsRequest, signal?: AbortSignal): Promise<SummaryResult>;
  /** One metric over time (`StatisticsQueries.MetricNames`), cut by a group (`GroupNames`) or not. */
  series(request: StatisticsRequest, metric: string, group: string | null, signal?: AbortSignal): Promise<SeriesResult>;
  /** A ranking of `tools`, `models`, `projects` or `sessions`, by `tokens`, `time` or `calls`. */
  top(request: StatisticsRequest, kind: string, by: string, signal?: AbortSignal): Promise<TopResult>;
  /** The table of tools. */
  tools(request: StatisticsRequest, signal?: AbortSignal): Promise<ToolsResult>;
  /** The table of models, and the models by effort. */
  models(request: StatisticsRequest, signal?: AbortSignal): Promise<ModelsResult>;
  /** The table of projects. */
  projects(request: StatisticsRequest, signal?: AbortSignal): Promise<ProjectsResult>;
  /** The sessions active in the period, sorted on `recent`, `time`, `tokens`, `calls` or `runs`. */
  sessions(request: StatisticsRequest, sort: string, signal?: AbortSignal): Promise<SessionsResult>;
  /** One session over its life, with its sub-agents when asked. `id` may be the start of the id. */
  session(id: string, withChildren: boolean, signal?: AbortSignal): Promise<SessionDetailResult>;
  /** A distribution (`run-duration`, `tool-duration`, `request-input`, `request-output`, `prompt-chars`, `prompt-words`, `run-cost`, `run-tool-calls`). */
  distribution(request: StatisticsRequest, measure: string, subject: string | null, signal?: AbortSignal): Promise<DistributionResult>;
  /** A year of days. */
  calendar(request: StatisticsRequest, signal?: AbortSignal): Promise<CalendarResult>;
  /** The day of the week by hour of the day. */
  weekHour(request: StatisticsRequest, signal?: AbortSignal): Promise<WeekHourResult>;
  /** The records of a period. */
  records(request: StatisticsRequest, signal?: AbortSignal): Promise<RecordsResult>;
  /** Errors, interrupted runs, compactions, the fill of the context. */
  health(request: StatisticsRequest, signal?: AbortSignal): Promise<HealthResult>;
  /** The counted names of a list: `shell-program`, `alta-command`, `changed-file-extension`, `skill`, `permission-mode`, `compaction-trigger`, `run-origin`, `session-origin`. */
  details(request: StatisticsRequest, list: string, signal?: AbortSignal): Promise<DetailsResult>;
  /** The runs of a period, sorted on `recent`, `longest`, `tokens` or `tools`. */
  runs(request: StatisticsRequest, sort: string, signal?: AbortSignal): Promise<RunsResult>;

  /**
   * An estimate of the cost from public prices, as a series of `cost-estimate` by model in dollars. Optional: the Cost page shows
   * its separate, marked block only when the binding provides it.
   */
  costEstimate?(request: StatisticsRequest, signal?: AbortSignal): Promise<SeriesResult>;

  /** The state of the reading of the history now. */
  status(signal?: AbortSignal): Promise<StatisticsStatus>;
  /** Chooses how much history to read, the first time or to read more. Resolves with the status after the choice. */
  chooseHistory(choice: HistoryChoice): Promise<StatisticsStatus>;
  /** Pauses the reading; the flow goes on. */
  pause(): Promise<StatisticsStatus>;
  /** Resumes a paused reading. */
  resume(): Promise<StatisticsStatus>;
  /** Stops the reading where it is: the statistics start at the date reached. */
  stopHere(): Promise<StatisticsStatus>;
  /** Removes the facts of the sessions whose journal is gone; resolves with how many sessions. */
  forgetDeleted(): Promise<number>;
  /**
   * Empties the statistics and comes back to the choice of how much history to read. Optional: the menu offers "Reset
   * statistics…" only when the binding provides it.
   */
  resetStatistics?(): Promise<StatisticsStatus>;

  /** Listens to status changes and to the days that changed. Returns the function that stops listening. */
  subscribe(listener: (event: StatisticsEvent) => void): () => void;
}

/** Where the canvas is opened, and what it may ask of the application. */
export type StatisticsContext = Readonly<{
  /** Keys what the canvas keeps for itself (the period, the filters, the page): one instance, one key. */
  instanceId: string;
  /** False while the canvas tab is hidden: it stops reading until it is shown again. */
  visible: boolean;
  /** The space the window shows: the canvas starts filtered on it, with "All spaces" one click away. */
  spaceId?: string | null;
  /** The projects of the spaces, for the space filter: the id the plugin knows, and the name to show. */
  spaces?: readonly Readonly<{ id: string; name: string; projectIds?: readonly string[] }>[];
  /** Starts the canvas filtered on this project (the menu of a project). */
  projectId?: string | null;
  projectName?: string | null;
  /** Opens a session in a tab of the window. */
  openSession?: (sessionId: string) => void;
  /** Where the canvas keeps its state; the browser's local storage when absent. */
  storage?: Pick<Storage, "getItem" | "setItem" | "removeItem"> | null;
  /** The name of today, `yyyy-MM-dd`, for tests; the clock of the page when absent. */
  today?: string;
}>;
