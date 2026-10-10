// A controlled summary over the real provider and query hook. Unlike the broad fixture, these answers have the session lifetime
// the backend returns, and can change without changing the request or remounting the canvas.
import { StrictMode } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { StatisticsApi, StatisticsContext, StatisticsEvent } from "./api";
import { createFixtureApi } from "./fixtureApi";
import { StatisticsProvider, useStatistics, type StatisticsRuntime } from "./runtime";
import type { StatisticsRequest, SummaryResult } from "./types";
import { queryKey, useStatisticsQuery } from "./useQuery";

type Answer = { total: number; from: string; to: string; unknown?: boolean };
type Options = Answer & { sessionId?: string | null; period?: string; deferFirst?: boolean };
const root = createRoot(document.getElementById("root")!);
const base = createFixtureApi({ today: "2026-10-10", scenario: "ready", sessionCount: 1, latencyMs: 0 });
const listeners = new Set<(event: StatisticsEvent) => void>();
const calls: StatisticsRequest[] = [];
let answer: Answer;
let options: Options;
let visible = true;
let deferred = false;
let release: (() => void) | null = null;
let runtime: StatisticsRuntime;

const api: StatisticsApi = {
  ...base,
  subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  summary(request) {
    calls.push(request);
    const data: SummaryResult = {
      query: { period: request.period, from: answer.from, to: answer.to, frequency: "day", timeZone: "UTC",
        coverage: { complete: true, historyState: "done" }, ignoredFilters: [], notes: answer.unknown ? ["session-not-found"] : [] },
      buckets: [], tiles: [{ id: "runs", unit: "count", value: answer.total, spark: [] }], costs: [],
    };
    if (!deferred) return Promise.resolve(data);
    deferred = false;
    return new Promise(resolve => { release = () => { release = null; resolve(data); }; });
  },
};

function Probe() {
  runtime = useStatistics();
  const request = runtime.request({ period: options.period ?? "all" });
  const result = useStatisticsQuery(queryKey("summary", request), signal => api.summary(request, signal), true, true);
  return <output id="answer">{JSON.stringify({ total: result.data?.tiles[0].value, header: runtime.header, periodDays: runtime.periodDays, refreshing: result.refreshing })}</output>;
}

function draw() {
  const context: StatisticsContext = { instanceId: "lifetime", sessionId: options.sessionId, visible, today: "2026-10-10", storage: null };
  flushSync(() => root.render(<StrictMode><StatisticsProvider api={api} context={context}><Probe /></StatisticsProvider></StrictMode>));
}

Object.assign(window, { lifetimeFixture: {
  mount(next: Options) {
    flushSync(() => root.render(null));
    options = next; answer = next; visible = true; deferred = next.deferFirst ?? false; release = null; calls.length = 0;
    draw();
  },
  change(next: Answer, fromDay: number, toDay: number, sessionIds: string[]) {
    answer = next;
    for (const listen of listeners) listen({ kind: "data", change: { revision: runtime.store.epoch + 1, fromDay, toDay, sessionIds } });
  },
  setVisible(next: boolean) { visible = next; draw(); },
  resolve() { release?.(); },
  calls: () => calls.slice(),
  epoch: () => runtime.store.epoch,
} });
