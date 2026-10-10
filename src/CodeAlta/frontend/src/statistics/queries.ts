import { useStatistics } from "./runtime";
import { queryKey, useStatisticsQuery, type QueryState } from "./useQuery";
import type {
  CalendarResult, DetailsResult, DistributionResult, HealthResult, ModelsResult, ProjectsResult, RecordsResult, RunsResult, SeriesResult, SessionsResult, StatisticsRequest,
  SummaryResult, ToolsResult, TopResult, WeekHourResult,
} from "./types";

// One hook per question of the plugin, with the request of the frame and what the page adds to it. A page calls them and draws
// what they return; `main` marks the question whose period the bar reports (the length of `all` and of `auto`).

/** What a page adds to the request of the frame, and whether the question is the main one of the page. */
export type QueryOptions = Readonly<{ extra?: Partial<StatisticsRequest>; main?: boolean; enabled?: boolean }>;

/** The tiles of the Overview. */
export function useSummary(options: QueryOptions = {}): QueryState<SummaryResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("summary", req), signal => api.summary(req, signal), options.enabled ?? true, options.main);
}

/** One metric over time. */
export function useSeries(metric: string, group: string | null, options: QueryOptions = {}): QueryState<SeriesResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("series", req, metric, group), signal => api.series(req, metric, group, signal), options.enabled ?? true, options.main);
}

/** A ranking. */
export function useTop(kind: string, by: string, options: QueryOptions = {}): QueryState<TopResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("top", req, kind, by), signal => api.top(req, kind, by, signal), options.enabled ?? true, options.main);
}

/** The table of tools. */
export function useTools(options: QueryOptions = {}): QueryState<ToolsResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("tools", req), signal => api.tools(req, signal), options.enabled ?? true, options.main);
}

/** The table of models. */
export function useModels(options: QueryOptions = {}): QueryState<ModelsResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("models", req), signal => api.models(req, signal), options.enabled ?? true, options.main);
}

/** The table of projects. */
export function useProjects(options: QueryOptions = {}): QueryState<ProjectsResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("projects", req), signal => api.projects(req, signal), options.enabled ?? true, options.main);
}

/** The sessions of the period. */
export function useSessions(sort: string, options: QueryOptions = {}): QueryState<SessionsResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("sessions", req, sort), signal => api.sessions(req, sort, signal), options.enabled ?? true, options.main);
}

/** A distribution. */
export function useDistribution(measure: string, subject: string | null, options: QueryOptions = {}): QueryState<DistributionResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("distribution", req, measure, subject), signal => api.distribution(req, measure, subject, signal), options.enabled ?? true, options.main);
}

/** The days of a year. */
export function useCalendar(options: QueryOptions = {}): QueryState<CalendarResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("calendar", req), signal => api.calendar(req, signal), options.enabled ?? true, options.main);
}

/** The day of the week by hour. */
export function useWeekHour(options: QueryOptions = {}): QueryState<WeekHourResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("weekHour", req), signal => api.weekHour(req, signal), options.enabled ?? true, options.main);
}

/** The records of the period. */
export function useRecords(options: QueryOptions = {}): QueryState<RecordsResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("records", req), signal => api.records(req, signal), options.enabled ?? true, options.main);
}

/** Errors, interrupted runs, compactions and the fill of the context. */
export function useHealth(options: QueryOptions = {}): QueryState<HealthResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("health", req), signal => api.health(req, signal), options.enabled ?? true, options.main);
}

/** The counted names of a list. */
export function useDetails(list: string, options: QueryOptions = {}): QueryState<DetailsResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("details", req, list), signal => api.details(req, list, signal), options.enabled ?? true, options.main);
}

/** The runs of the period. */
export function useRuns(sort: string, options: QueryOptions = {}): QueryState<RunsResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("runs", req, sort), signal => api.runs(req, sort, signal), options.enabled ?? true, options.main);
}

/** The estimate of the cost, when the binding offers one. */
export function useCostEstimate(options: QueryOptions = {}): QueryState<SeriesResult> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  const estimate = api.costEstimate;
  return useStatisticsQuery(queryKey("costEstimate", req), signal => estimate!.call(api, req, signal), (options.enabled ?? true) && estimate !== undefined, options.main);
}

/** The distribution of the duration of several tools at once (one question each), for the boxes of the Tools page. */
export function useToolDurations(tools: readonly string[], options: QueryOptions = {}): QueryState<readonly DistributionResult[]> {
  const { api, request } = useStatistics();
  const req = request(options.extra);
  return useStatisticsQuery(queryKey("toolDurations", req, tools), signal => Promise.all(tools.map(tool => api.distribution(req, "tool-duration", tool, signal))),
    (options.enabled ?? true) && tools.length > 0, options.main);
}
