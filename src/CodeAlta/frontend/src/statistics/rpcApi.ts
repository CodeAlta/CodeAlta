import type { AltaRpc } from "../pluginScript/alta";
import type { StatisticsApi, StatisticsEvent } from "./api";
import type {
  CalendarResult, DetailsResult, DistributionResult, HealthResult, HistoryChoice, ModelsResult, ProjectsResult, RecordsResult, RunsResult, SeriesResult,
  SessionDetailResult, SessionsResult, StatisticsDataChange, StatisticsRequest, StatisticsStatus, SummaryResult, ToolsResult, TopResult, WeekHourResult,
} from "./types";

// The StatisticsApi of the canvas over `alta.rpc`: each method is one call of the Statistics plugin (`statistics.<name>`), and the results are the JSON the
// plugin writes (`StatisticsJson`), read as they are. The plugin tells what changed with the event `statistics.events`.

/** The name of the event the plugin sends its changes on. */
export const eventsName = "statistics.events";

/** A change that touches every day: what the canvas applies when it cannot know what it missed (the connection to the plugin was made again). */
export const everyDay: StatisticsDataChange = Object.freeze({ revision: 0, fromDay: 10101, toDay: 99991231, sessionIds: Object.freeze([]) as readonly string[] });

/** The spaces and the projects the plugin knows, for the filters of the canvas. */
export type StatisticsDirectory = Readonly<{
  spaces: readonly Readonly<{ id: string; name: string; isDefault: boolean; projectIds: readonly string[] }>[];
  projects: readonly Readonly<{ id: string; name: string }>[];
}>;

/** The API of the canvas, and what else the application asks of the plugin. */
export type RpcStatisticsApi = StatisticsApi & Readonly<{
  /** The spaces with their projects, and the projects with their names. */
  directory(signal?: AbortSignal): Promise<StatisticsDirectory>;
}>;

const abortError = () => new DOMException("The question was canceled.", "AbortError");

/** Whether an error is the end of a question that was canceled: the canvas ignores those. */
function canceled(error: unknown, signal: AbortSignal | undefined): boolean {
  return signal?.aborted === true || (typeof error === "object" && error !== null && ((error as { code?: unknown }).code === "operation_canceled" || (error as { name?: unknown }).name === "AbortError"));
}

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);

/** How long the canvas waits before it asks to listen again when the plugin could not be reached, and the longest wait: it doubles each time. */
export const listenRetryMilliseconds = 500;
const listenRetryLimitMilliseconds = 8000;

/** What `createRpcApi` takes besides the calls: a test waits less. */
export type RpcApiOptions = Readonly<{ retryMilliseconds?: number }>;

/** Reads what the plugin sent on its event: a status, or the days that changed; anything else is not an event of the statistics. */
export function readEvent(value: unknown): StatisticsEvent | null {
  if (!isRecord(value)) return null;
  if (value.kind === "status" && isRecord(value.status) && typeof value.status.state === "string") return { kind: "status", status: value.status as StatisticsStatus };
  if (value.kind === "data" && isRecord(value.change) && typeof value.change.fromDay === "number" && typeof value.change.toDay === "number") {
    return { kind: "data", change: { revision: Number(value.change.revision) || 0, fromDay: value.change.fromDay, toDay: value.change.toDay, sessionIds: Array.isArray(value.change.sessionIds) ? value.change.sessionIds.filter((id): id is string => typeof id === "string") : [] } };
  }

  return null;
}

/**
 * Makes the API of the Statistics canvas over the calls of its plugin. A call that was canceled rejects with an `AbortError`; any other failure rejects with the
 * `AltaError` of the call, whose code (`invalid_request`, `unavailable`, `result_too_large`, `timeout`, ...) the canvas shows as it is.
 */
export function createRpcApi(rpc: AltaRpc, options: RpcApiOptions = {}): RpcStatisticsApi {
  const retryMilliseconds = options.retryMilliseconds ?? listenRetryMilliseconds;
  async function call<T>(name: string, input: Record<string, unknown>, signal?: AbortSignal): Promise<T> {
    if (signal?.aborted) throw abortError();
    try {
      return await rpc.invoke(name, input, { signal }) as T;
    } catch (error) {
      if (canceled(error, signal)) throw abortError();
      throw error;
    }
  }

  const question = <T>(name: string, request: StatisticsRequest, extra: Record<string, unknown>, signal: AbortSignal | undefined) => call<T>(`statistics.${name}`, { request, ...extra }, signal);

  return {
    summary: (request, signal) => question<SummaryResult>("summary", request, {}, signal),
    series: (request, metric, group, signal) => question<SeriesResult>("series", request, { metric, group }, signal),
    top: (request, kind, by, signal) => question<TopResult>("top", request, { kind, by }, signal),
    tools: (request, signal) => question<ToolsResult>("tools", request, {}, signal),
    models: (request, signal) => question<ModelsResult>("models", request, {}, signal),
    projects: (request, signal) => question<ProjectsResult>("projects", request, {}, signal),
    sessions: (request, sort, signal) => question<SessionsResult>("sessions", request, { sort }, signal),
    session: (id, withChildren, signal) => call<SessionDetailResult>("statistics.session", { id, withChildren }, signal),
    distribution: (request, measure, subject, signal) => question<DistributionResult>("distribution", request, { measure, subject }, signal),
    calendar: (request, signal) => question<CalendarResult>("calendar", request, {}, signal),
    weekHour: (request, signal) => question<WeekHourResult>("week-hour", request, {}, signal),
    records: (request, signal) => question<RecordsResult>("records", request, {}, signal),
    health: (request, signal) => question<HealthResult>("health", request, {}, signal),
    details: (request, list, signal) => question<DetailsResult>("details", request, { list }, signal),
    runs: (request, sort, signal) => question<RunsResult>("runs", request, { sort }, signal),

    status: signal => call<StatisticsStatus>("statistics.status", {}, signal),
    chooseHistory: (choice: HistoryChoice) => call<StatisticsStatus>("statistics.choose-history", choice.kind === "days" ? { kind: choice.kind, days: choice.days } : { kind: choice.kind }),
    pause: () => call<StatisticsStatus>("statistics.pause", {}),
    resume: () => call<StatisticsStatus>("statistics.resume", {}),
    stopHere: () => call<StatisticsStatus>("statistics.stop-here", {}),
    forgetDeleted: async () => (await call<{ count: number }>("statistics.forget-deleted", {})).count,
    resetStatistics: () => call<StatisticsStatus>("statistics.reset", {}),
    directory: signal => call<StatisticsDirectory>("statistics.context", {}, signal),

    subscribe(listener) {
      let alive = true;
      let stop: (() => void) | null = null;
      let generation = rpc.generation.value;
      let latest = 0;
      let failures = 0;
      let timer: ReturnType<typeof setTimeout> | undefined;

      // Asks to listen again after a wait that grows: the plugin could not be reached, or the connection that carried the listening ended.
      const again = () => {
        if (!alive || timer !== undefined) return;
        timer = setTimeout(() => { timer = undefined; void listen(true); }, Math.min(listenRetryLimitMilliseconds, retryMilliseconds * 2 ** Math.min(failures++, 4)));
      };

      // Listens to the event, then reads what may have been missed before the listening began.
      const listen = async (catchUp: boolean) => {
        const ticket = ++latest;
        if (timer !== undefined) { clearTimeout(timer); timer = undefined; }
        let listening = false;
        try {
          const off = await rpc.subscribe(eventsName, value => {
            const event = readEvent(value);
            if (alive && event) listener(event);
          });
          if (!alive || ticket !== latest) { off(); return; }
          stop?.();
          stop = off;
          listening = true;
          failures = 0;
          if (!catchUp) return;
          // The plugin may have been reloaded, or the page reconnected: the numbers on screen are stale and the status may be another.
          listener({ kind: "data", change: everyDay });
          const status = await call<StatisticsStatus>("statistics.status", {});
          if (alive && ticket === latest) listener({ kind: "status", status });
        } catch (error) {
          // The window cannot reach the plugin now (it starts, it reconnects): the canvas asks again. Where the window carries no calls, nobody listens.
          if (alive && ticket === latest && !listening && (error as { retryable?: unknown } | null)?.retryable === true) again();
        }
      };

      void listen(false);
      const offGeneration = rpc.generation.subscribe(next => {
        if (next === generation) return;
        generation = next;
        void listen(true);
      });
      // A connection that ends takes the listening with it, and the canvas makes no call of its own that would tell it: it asks to listen again.
      const offConnected = rpc.connected?.subscribe(open => {
        if (open || !stop) return;
        stop = null;
        again();
      });
      return () => {
        alive = false;
        latest++;
        if (timer !== undefined) { clearTimeout(timer); timer = undefined; }
        offGeneration();
        offConnected?.();
        stop?.();
        stop = null;
      };
    },
  };
}
