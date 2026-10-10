import type { StatisticsApi, StatisticsEvent } from "./api";
import { addDays, addMonths, allowedFrequencies, dayDistance, resolveFrequency } from "./frame";
import { fixtureTools, generateFixtureData, type Cell, type FixtureData, type ToolCell } from "./fixtureData";
import type {
  BucketInfo, CalendarResult, CostAmount, DetailsResult, DistributionResult, DistributionStep, Frequency, HealthResult, HistoryChoice, ModelRow, ModelsResult,
  ProjectRow, ProjectsResult, QueryHeader, RankedRow, RecordEntry, RecordsResult, RunSample, RunsResult, SeriesLine, SeriesResult, SessionDetailResult, SessionEntry,
  SessionsResult, StatisticsRequest, StatisticsStatus, SummaryResult, SummaryTile, ToolRow, ToolsResult, TopResult, WeekHourResult,
} from "./types";

// A StatisticsApi over generated data: every question is answered by adding up the cells of `fixtureData.ts`, with the same
// periods, buckets, filters and comparisons the plugin has, so a canvas bound to it behaves like the real one. Scenarios start
// the history in each of its states; `control` moves it along, fails a question or sends a change, for tests and for the demo.

/** The state the history starts in. */
export type FixtureScenario = "ready" | "first-time" | "reading" | "paused" | "stopped" | "stopped-skipped" | "failed" | "skipped";

/** What `createFixtureApi` takes. */
export type FixtureOptions = Readonly<{
  /** Today, `yyyy-MM-dd` (default 2026-10-09, so tests do not move with the clock). */
  today?: string;
  scenario?: FixtureScenario;
  /** Sessions generated (default 906). */
  sessionCount?: number;
  /** A time every answer waits, in milliseconds (default 0). */
  latencyMs?: number;
  seed?: number;
  /** Whether the Cost page may show an estimate block (default false: no `costEstimate` method). */
  estimates?: boolean;
  /** Pre-built data, to share between fixtures. */
  data?: FixtureData;
}>;

/** What a test or a demo does to the fixture. */
export type FixtureControl = Readonly<{
  data: FixtureData;
  status: () => StatisticsStatus;
  /** Replaces parts of the status and tells the listeners. */
  setStatus: (patch: Partial<StatisticsStatus>) => void;
  /** Reads `sessions` more sessions of the history (default 100); finishes the reading when all are read. */
  advance: (sessions?: number) => void;
  /** Tells the listeners that the numbers of some days changed. */
  emitData: (fromDay?: number, toDay?: number) => void;
  /** Rejects the next `count` calls of a method (`*` for any) with `message`. */
  failNext: (method: string, message?: string, count?: number) => void;
  setLatency: (milliseconds: number) => void;
  listenerCount: () => number;
}>;

/** A fixture API with its control. */
export type FixtureApi = StatisticsApi & Readonly<{ control: FixtureControl }>;

const dayNumber = (day: string) => Number(day.replaceAll("-", ""));
const dayOfNumber = (value: number) => { const text = String(value); return `${text.slice(0, 4)}-${text.slice(4, 6)}-${text.slice(6, 8)}`; };
const weekStartIndex = (name: string | undefined) => { const index = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].indexOf(name ?? "Monday"); return index < 0 ? 1 : index; };
const dayOfWeek = (day: string) => new Date(`${day}T00:00:00Z`).getUTCDay();

const aborted = () => Object.assign(new Error("The question was canceled."), { name: "AbortError" });

type Family = "activity" | "usage" | "tools" | "content" | "cost";
type Range = Readonly<{ from: string; to: string }>;
type Plan = Readonly<{ range: Range; frequency: Frequency; buckets: readonly BucketInfo[]; indexOf: (day: string, hour: number) => number }>;

type Resolved = Readonly<{
  request: StatisticsRequest;
  range: Range;
  frequency: Frequency;
  plan: Plan;
  compare: Plan | null;
  weekStart: number;
  limit: number | undefined;
}>;

function buildPlan(range: Range, frequency: Frequency, weekStart: number): Plan {
  const buckets: BucketInfo[] = [];
  const index = new Map<string, number>();
  const add = (key: string, start: string, label: string) => { index.set(key, buckets.length); buckets.push({ index: buckets.length, start, label }); };
  const days = dayDistance(range.from, range.to) + 1;
  switch (frequency) {
    case "hour":
      for (let offset = 0; offset < days; offset++) {
        const day = addDays(range.from, offset);
        for (let hour = 0; hour < 24; hour++) add(`${day}T${hour}`, `${day}T${String(hour).padStart(2, "0")}:00`, String(hour).padStart(2, "0"));
      }
      return { range, frequency, buckets, indexOf: (day, hour) => index.get(`${day}T${hour}`) ?? -1 };
    case "day":
      for (let offset = 0; offset < days; offset++) { const day = addDays(range.from, offset); add(day, `${day}T00:00`, day.slice(5)); }
      return { range, frequency, buckets, indexOf: day => index.get(day) ?? -1 };
    case "week": {
      const floor = (day: string) => addDays(day, -((dayOfWeek(day) - weekStart + 7) % 7));
      for (let start = floor(range.from); start <= range.to; start = addDays(start, 7)) add(start, `${start}T00:00`, start.slice(5));
      return { range, frequency, buckets, indexOf: day => index.get(floor(day)) ?? -1 };
    }
    case "month":
      for (let start = `${range.from.slice(0, 7)}-01`; start <= range.to; start = addMonths(start, 1)) add(start.slice(0, 7), `${start}T00:00`, start.slice(0, 7));
      return { range, frequency, buckets, indexOf: day => index.get(day.slice(0, 7)) ?? -1 };
    case "year":
      for (let year = Number(range.from.slice(0, 4)); year <= Number(range.to.slice(0, 4)); year++) add(String(year), `${year}-01-01T00:00`, String(year));
      return { range, frequency, buckets, indexOf: day => index.get(day.slice(0, 4)) ?? -1 };
  }
}

type Lines = Map<string, { label: string; values: number[]; previous: number[] | null }>;

/**
 * Creates the fixture API. Every call returns a promise that settles after `latencyMs`, honors its signal, and can be failed
 * by `control.failNext`; the data are generated once.
 */
export function createFixtureApi(options: FixtureOptions = {}): FixtureApi {
  const today = options.today ?? "2026-10-09";
  const data = options.data ?? generateFixtureData({ today, sessionCount: options.sessionCount, seed: options.seed });
  const total = { sessions: data.sessions.length, bytes: 10_840_000_000 };
  const listeners = new Set<(event: StatisticsEvent) => void>();
  const failures: { method: string; message: string; count: number }[] = [];
  let latency = options.latencyMs ?? 0;
  let revision = 1;

  const firstDayNumber = dayNumber(data.firstDay);
  const baseStatus = (patch: Partial<StatisticsStatus>): StatisticsStatus => ({
    state: "done", sessionsTotal: total.sessions, sessionsDone: total.sessions, bytesTotal: total.bytes, bytesDone: total.bytes, skippedCount: 0, skipped: [], pendingFlow: 0, revision,
    choice: "all", oldestDateReached: firstDayNumber, isComplete: true, ...patch,
  });
  // Made when a scenario asks for it: a fixture of one session has no fourth one.
  const skippedSessions = () => [{ sessionId: data.sessions[3].id, reason: "The journal is damaged at its line 1204." }, { sessionId: data.sessions[9].id, reason: "The file is locked by another program." },
    { sessionId: data.sessions[17].id, reason: "A record is larger than 256 MB." }];
  const scenarios: Record<FixtureScenario, () => StatisticsStatus> = {
    ready: () => baseStatus({}),
    "first-time": () => ({ state: "needsChoice", sessionsTotal: total.sessions, sessionsDone: 0, bytesTotal: total.bytes, bytesDone: 0, oldestDateReached: firstDayNumber, skippedCount: 0, skipped: [], pendingFlow: 0, revision, isComplete: false }),
    reading: () => baseStatus({ state: "reading", reason: "first-read", sessionsDone: 312, bytesDone: Math.round(total.bytes * 0.34), oldestDateReached: dayNumber("2026-07-14"), completeFromDay: dayNumber("2026-07-14"),
      bytesPerSecond: 480_000_000, etaSeconds: 40, currentSessionId: data.sessions[40]?.id, isComplete: false }),
    paused: () => baseStatus({ state: "paused", reason: "first-read", sessionsDone: 312, bytesDone: Math.round(total.bytes * 0.34), oldestDateReached: dayNumber("2026-07-14"), completeFromDay: dayNumber("2026-07-14"), isComplete: false }),
    stopped: () => baseStatus({ state: "stoppedHere", sessionsDone: 312, bytesDone: Math.round(total.bytes * 0.34), oldestDateReached: dayNumber("2026-07-14"), completeFromDay: dayNumber("2026-07-14"), floorDay: dayNumber("2026-07-14"), isComplete: false }),
    failed: () => ({ state: "failed", sessionsTotal: 0, sessionsDone: 0, bytesTotal: 0, bytesDone: 0, skippedCount: 0, skipped: [], pendingFlow: 0, revision, error: "The statistics database could not be opened.", isComplete: false }),
    skipped: () => baseStatus({ skippedCount: 3, skipped: skippedSessions() }),
    "stopped-skipped": () => ({ ...scenarios.stopped(), skippedCount: 3, skipped: skippedSessions() }),
  };
  let status = scenarios[options.scenario ?? "ready"]();

  const emit = (event: StatisticsEvent) => { for (const listener of [...listeners]) listener(event); };
  const setStatus = (patch: Partial<StatisticsStatus>) => { status = { ...status, ...patch, revision }; emit({ kind: "status", status }); };

  // The first local day the numbers rest on: nothing before it was read.
  const availableFrom = (): string => {
    if (status.state === "needsChoice" || status.state === "starting" || status.state === "failed") return today;
    if (status.completeFromDay) return dayOfNumber(status.completeFromDay);
    if (status.floorDay) return dayOfNumber(status.floorDay);
    return data.firstDay;
  };

  const spaceProjects = (value: string): ReadonlySet<string> | null => {
    const space = data.spaces.find(item => item.id === value || item.name.toLowerCase() === value.toLowerCase());
    return space ? new Set(space.projects) : null;
  };
  const projectId = (value: string): string | null => data.projects.find(project => project.id === value || project.name.toLowerCase() === value.toLowerCase())?.id ?? null;
  const projectName = (id: string) => data.projects.find(project => project.id === id)?.name ?? id;

  // Which filters each family honors; the others are named in `ignoredFilters`.
  const honored: Record<Family, readonly string[]> = {
    activity: ["space", "project", "provider", "model", "effort", "origin"],
    usage: ["space", "project", "provider", "model", "effort"],
    cost: ["space", "project", "provider", "model", "effort"],
    tools: ["space", "project", "toolKind"],
    content: ["space", "project", "origin"],
  };

  function resolve(request: StatisticsRequest): Resolved {
    const weekStart = weekStartIndex(request.weekStart);
    const range = periodRange(request.period);
    const days = dayDistance(range.from, range.to) + 1;
    const frequency = resolveFrequency(request.frequency ?? "auto", days);
    if (request.frequency && request.frequency !== "auto" && !allowedFrequencies(days).includes(request.frequency) && days > 800) throw new Error(`The frequency ${request.frequency} gives too many buckets for ${days} days.`);
    const plan = buildPlan(range, frequency, weekStart);
    let compare: Plan | null = null;
    if (request.comparison === "previousPeriod") compare = buildPlan({ from: addDays(range.from, -days), to: addDays(range.from, -1) }, frequency, weekStart);
    else if (request.comparison === "samePeriodLastYear") compare = buildPlan({ from: addMonths(range.from, -12), to: addMonths(range.to, -12) }, frequency, weekStart);
    return { request, range, frequency, plan, compare, weekStart, limit: request.limit };
  }

  function periodRange(period: string): Range {
    const text = period.trim().toLowerCase();
    switch (text) {
      case "today": return { from: today, to: today };
      case "yesterday": return { from: addDays(today, -1), to: addDays(today, -1) };
      case "week": return { from: addDays(today, -((dayOfWeek(today) + 6) % 7)), to: today };
      case "month": return { from: `${today.slice(0, 7)}-01`, to: today };
      case "last-month": { const first = addMonths(`${today.slice(0, 7)}-01`, -1); return { from: first, to: addDays(addMonths(first, 1), -1) }; }
      case "year": return { from: `${today.slice(0, 4)}-01-01`, to: today };
      case "all": return { from: availableFrom() > data.firstDay ? availableFrom() : data.firstDay, to: today };
    }
    const count = /^(\d+)d$/.exec(text);
    if (count) return { from: addDays(today, 1 - Number(count[1])), to: today };
    const split = text.split("..");
    if (split.length === 2 && /^\d{4}-\d{2}-\d{2}$/.test(split[0]) && (split[1] === "" || /^\d{4}-\d{2}-\d{2}$/.test(split[1]))) {
      const to = split[1] || today;
      if (to < split[0]) throw new Error(`The period '${period}' ends before it starts.`);
      return { from: split[0], to };
    }
    throw new Error(`'${period}' is not a period.`);
  }

  function header(resolved: Resolved, families: readonly Family[]): QueryHeader {
    const filter = resolved.request.filter ?? {};
    const set = Object.keys(filter).filter(key => (filter as Record<string, unknown>)[key] !== undefined);
    const ignored = set.filter(key => families.every(family => !honored[family].includes(key)));
    const completeFrom = status.state === "done" && !status.completeFromDay ? undefined : availableFrom();
    const needsChoice = status.state === "needsChoice" || status.state === "starting" || status.state === "failed";
    const complete = !needsChoice && (completeFrom === undefined || resolved.range.from >= completeFrom);
    const historyState = needsChoice ? "needs-choice" : status.state === "reading" ? "reading" : status.state === "paused" ? "paused" : status.state === "stoppedHere" ? "stopped" : "done";
    return {
      period: resolved.request.period, from: resolved.range.from, to: resolved.range.to, frequency: resolved.frequency, timeZone: "Europe/Paris",
      ...(resolved.compare ? { compareFrom: resolved.compare.range.from, compareTo: resolved.compare.range.to } : {}),
      coverage: { complete, historyState, ...(completeFrom && !needsChoice ? { completeFrom } : {}) },
      ignoredFilters: ignored, notes: set.includes("space") ? ["space-membership-is-current"] : [],
    };
  }

  function accepts(cell: Cell, request: StatisticsRequest, family: Family): boolean {
    const filter = request.filter ?? {};
    if (filter.space) { const projects = spaceProjects(filter.space); if (projects && !projects.has(cell.project)) return false; }
    if (filter.project) { const id = projectId(filter.project); if (id && cell.project !== id) return false; }
    if (honored[family].includes("provider") && filter.provider && cell.provider !== filter.provider) return false;
    if (honored[family].includes("model") && filter.model && cell.model !== filter.model) return false;
    if (honored[family].includes("effort") && filter.effort && cell.effort !== filter.effort) return false;
    if (honored[family].includes("origin") && filter.origin && cell.origin !== filter.origin) return false;
    return true;
  }

  const readable = (cell: Cell) => cell.day >= availableFrom();
  const within = (cell: Cell, range: Range) => cell.day >= range.from && cell.day <= range.to && readable(cell);
  function cellsOf(resolved: Resolved, family: Family, plan: Plan = resolved.plan): Cell[] {
    return data.cells.filter(cell => within(cell, plan.range) && accepts(cell, resolved.request, family));
  }

  // The key and the label of a group, as the plugin gives them: a group of a fixed list (who sent a prompt, how it came, the kind of
  // a tool) is keyed by its name, which is the value a filter takes.
  const groupOf = (cell: Cell, group: string | null, tool?: ToolCell): [string, string] => {
    switch (group) {
      case "provider": return [cell.provider, cell.provider];
      case "model": return [cell.model, cell.model];
      case "effort": return [cell.effort, cell.effort];
      case "project": return [cell.project, projectName(cell.project)];
      case "delegated": return cell.delegated ? ["sub-agent", "Sub-agents"] : ["direct", "Direct"];
      case "origin": return [cell.origin, cell.origin];
      case "prompt-kind": return cell.origin === "you" ? ["newturn", "newturn"] : ["queued", "queued"];
      case "tool": return [tool?.tool ?? "", tool?.tool ?? ""];
      case "kind": return [tool?.kind ?? "", tool?.kind ?? ""];
      case "unit": return [cell.costUnit ?? "none", cell.costUnit ?? "none"];
      default: return ["", ""];
    }
  };

  type Metric = Readonly<{ family: Family; unit: string; value: (cell: Cell, tool?: ToolCell) => number; perTool?: boolean }>;
  const sum = (items: readonly number[]) => items.reduce((total, value) => total + value, 0);
  const metrics: Record<string, Metric> = {
    runs: { family: "activity", unit: "count", value: cell => cell.runs },
    "runs-completed": { family: "activity", unit: "count", value: cell => cell.completed },
    "runs-failed": { family: "activity", unit: "count", value: cell => cell.failed },
    "runs-interrupted": { family: "activity", unit: "count", value: cell => cell.interrupted },
    "active-time": { family: "activity", unit: "ms", value: cell => cell.activeMs },
    errors: { family: "activity", unit: "count", value: cell => cell.errors },
    compactions: { family: "activity", unit: "count", value: cell => cell.compactions },
    requests: { family: "usage", unit: "count", value: cell => cell.requests },
    tokens: { family: "usage", unit: "tokens", value: cell => cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output },
    "input-tokens": { family: "usage", unit: "tokens", value: cell => cell.fresh + cell.cacheRead + cell.cacheWrite },
    "fresh-input-tokens": { family: "usage", unit: "tokens", value: cell => cell.fresh },
    "cache-read-tokens": { family: "usage", unit: "tokens", value: cell => cell.cacheRead },
    "cache-write-tokens": { family: "usage", unit: "tokens", value: cell => cell.cacheWrite },
    "output-tokens": { family: "usage", unit: "tokens", value: cell => cell.output },
    "reasoning-tokens": { family: "usage", unit: "tokens", value: cell => cell.reasoning },
    "context-samples": { family: "usage", unit: "count", value: cell => cell.requests },
    "tool-calls": { family: "tools", unit: "count", perTool: true, value: (_, tool) => tool?.calls ?? 0 },
    "tool-failures": { family: "tools", unit: "count", perTool: true, value: (_, tool) => tool?.failures ?? 0 },
    "tool-time": { family: "tools", unit: "ms", perTool: true, value: (_, tool) => tool?.timeMs ?? 0 },
    "tool-bytes-in": { family: "tools", unit: "bytes", perTool: true, value: (_, tool) => tool?.bytesIn ?? 0 },
    "tool-bytes-out": { family: "tools", unit: "bytes", perTool: true, value: (_, tool) => tool?.bytesOut ?? 0 },
    "files-changed": { family: "tools", unit: "count", perTool: true, value: (_, tool) => tool?.filesChanged ?? 0 },
    "lines-added": { family: "tools", unit: "lines", perTool: true, value: (_, tool) => tool?.linesAdded ?? 0 },
    "lines-removed": { family: "tools", unit: "lines", perTool: true, value: (_, tool) => tool?.linesRemoved ?? 0 },
    prompts: { family: "content", unit: "count", value: cell => cell.promptsYou + cell.promptsOther },
    "your-prompts": { family: "content", unit: "count", value: cell => cell.promptsYou },
    "prompt-chars": { family: "content", unit: "count", value: cell => cell.promptChars },
    "prompt-words": { family: "content", unit: "count", value: cell => cell.promptWords },
    "prompt-files": { family: "content", unit: "count", value: cell => cell.attachments[0] },
    "prompt-images": { family: "content", unit: "count", value: cell => cell.attachments[1] },
    "prompt-directories": { family: "content", unit: "count", value: cell => cell.attachments[2] },
    answers: { family: "content", unit: "count", value: cell => cell.answers },
    cost: { family: "cost", unit: "cost", value: cell => cell.cost },
  };

  function accumulate(plan: Plan, resolved: Resolved, metric: Metric, group: string | null, lines: Lines, previous: boolean) {
    const family = metric.family;
    const count = resolved.plan.buckets.length;
    for (const cell of cellsOf(resolved, family, plan)) {
      const at = plan.indexOf(cell.day, cell.hour);
      if (at < 0 || at >= count) continue;
      const rows: (readonly [Cell, ToolCell | undefined])[] = metric.perTool ? cell.tools.filter(tool => !(resolved.request.filter?.toolKind) || tool.kind === resolved.request.filter.toolKind).map(tool => [cell, tool] as const) : [[cell, undefined]];
      for (const [row, tool] of rows) {
        if (metric.family === "cost" && row.costUnit === null) continue;
        const [key, label] = metric.family === "cost" ? groupOf(row, "unit") : groupOf(row, group, tool);
        let line = lines.get(key);
        if (!line) { line = { label, values: new Array(count).fill(0), previous: resolved.compare ? new Array(count).fill(0) : null }; lines.set(key, line); }
        const target = previous ? line.previous : line.values;
        if (target) target[at] += metric.value(row, tool);
      }
    }
  }

  function toLines(lines: Lines, limit = 20): SeriesLine[] {
    const all = [...lines.entries()].map(([key, line]) => ({ key, label: line.label, values: line.values.map(round), previous: line.previous?.map(round), total: round(sum(line.values)), previousTotal: line.previous ? round(sum(line.previous)) : undefined }))
      .sort((a, b) => b.total - a.total || a.label.localeCompare(b.label));
    if (all.length <= limit) return all;
    const kept = all.slice(0, limit - 1), rest = all.slice(limit - 1);
    const merge = (pick: (line: typeof all[number]) => readonly number[] | undefined) => kept[0].values.map((_, index) => round(sum(rest.map(line => pick(line)?.[index] ?? 0))));
    return [...kept, { key: "other", label: "other", values: merge(line => line.values), previous: rest[0]?.previous ? merge(line => line.previous) : undefined,
      total: round(sum(rest.map(line => line.total))), previousTotal: rest[0]?.previousTotal === undefined ? undefined : round(sum(rest.map(line => line.previousTotal ?? 0))) }];
  }
  const round = (value: number) => Math.round(value * 100) / 100;

  function sessionsActive(resolved: Resolved, started: boolean, group: string | null): SeriesResult {
    const lines: Lines = new Map();
    const count = resolved.plan.buckets.length;
    const run = (plan: Plan, previous: boolean) => {
      const seen = new Set<string>();
      for (const cell of cellsOf(resolved, "activity", plan)) {
        const at = plan.indexOf(cell.day, cell.hour);
        if (at < 0 || at >= count) continue;
        if (started) { const session = data.sessions.find(item => item.id === cell.session); if (!session || session.first !== cell.day) continue; }
        const [key, label] = groupOf(cell, group);
        const mark = `${at}|${key}|${cell.session}`;
        if (seen.has(mark)) continue;
        seen.add(mark);
        let line = lines.get(key);
        if (!line) { line = { label, values: new Array(count).fill(0), previous: resolved.compare ? new Array(count).fill(0) : null }; lines.set(key, line); }
        (previous ? line.previous : line.values)![at] += 1;
      }
    };
    run(resolved.plan, false);
    if (resolved.compare) run(resolved.compare, true);
    return { query: header(resolved, ["activity"]), metric: started ? "sessions-started" : "sessions-active", unit: "count", ...(group ? { group } : {}), buckets: resolved.plan.buckets, series: toLines(lines, resolved.limit ?? 20) };
  }

  // The most sessions at work in the same hour, in each bucket, as the plugin gives the most sessions with a run going at the same
  // moment: one line, whose total is the most of the period.
  function sessionsAtOnce(resolved: Resolved): SeriesResult {
    const count = resolved.plan.buckets.length;
    const most = (plan: Plan) => {
      const hours = new Map<string, Set<string>>();
      const values = new Array<number>(count).fill(0);
      for (const cell of cellsOf(resolved, "activity", plan)) {
        const at = plan.indexOf(cell.day, cell.hour);
        if (at < 0 || at >= count) continue;
        const key = `${cell.day}T${cell.hour}`;
        let sessions = hours.get(key);
        if (!sessions) { sessions = new Set(); hours.set(key, sessions); }
        sessions.add(cell.session);
        values[at] = Math.max(values[at], sessions.size);
      }
      return values;
    };
    const values = most(resolved.plan), previous = resolved.compare ? most(resolved.compare) : undefined;
    return { query: header(resolved, ["activity"]), metric: "sessions-at-once", unit: "count", buckets: resolved.plan.buckets,
      series: [{ key: "", label: "total", values, total: Math.max(0, ...values), ...(previous ? { previous, previousTotal: Math.max(0, ...previous) } : {}) }] };
  }

  // The average fill of the context window: the sum of the fills of the requests over their number, in each bucket and over the
  // period, as the plugin computes it. A bucket without a request is 0, and the metric of the samples says which.
  function contextFill(resolved: Resolved, group: string | null): SeriesResult {
    const count = resolved.plan.buckets.length;
    const lines = new Map<string, { label: string; sums: number[]; samples: number[] }>();
    for (const cell of cellsOf(resolved, "usage")) {
      const at = resolved.plan.indexOf(cell.day, cell.hour);
      if (at < 0 || at >= count) continue;
      const [key, label] = groupOf(cell, group);
      let line = lines.get(key);
      if (!line) { line = { label: group ? label : "total", sums: new Array(count).fill(0), samples: new Array(count).fill(0) }; lines.set(key, line); }
      line.sums[at] += cell.contextFill * cell.requests;
      line.samples[at] += cell.requests;
    }
    const all = [...lines.entries()].map(([key, line]) => ({ key, ...line })).sort((a, b) => sum(b.samples) - sum(a.samples) || a.key.localeCompare(b.key));
    const limit = resolved.limit ?? 20;
    const kept = all.slice(0, limit), rest = all.slice(limit);
    if (rest.length > 0) kept.push({ key: "other", label: "other", sums: kept[0].sums.map((_, at) => sum(rest.map(line => line.sums[at]))), samples: kept[0].samples.map((_, at) => sum(rest.map(line => line.samples[at]))) });
    const average = (total: number, samples: number) => samples > 0 ? total / samples : 0;
    return { query: header(resolved, ["usage"]), metric: "context-fill", unit: "ratio", ...(group ? { group } : {}), buckets: resolved.plan.buckets,
      series: kept.map(line => ({ key: line.key, label: line.label, values: line.sums.map((value, at) => average(value, line.samples[at])), total: average(sum(line.sums), sum(line.samples)) })) };
  }

  // How far below a session of its own each sub-agent that started in the period is, following the parents.
  function subAgentDepth(resolved: Resolved): DetailsResult {
    const byId = new Map(data.sessions.map(session => [session.id, session]));
    const counts = new Map<number, number>();
    for (const session of data.sessions) {
      if (!session.parent || session.first < resolved.plan.range.from || session.first > resolved.plan.range.to) continue;
      let depth = 0;
      for (let current: typeof session | undefined = session; current?.parent && depth < 64; current = byId.get(current.parent)) depth++;
      counts.set(depth, (counts.get(depth) ?? 0) + 1);
    }
    const total = sum([...counts.values()]);
    const rows = [...counts.entries()].sort((a, b) => a[0] - b[0]).map(([depth, count]) => ({ name: String(depth), count, share: total ? count / total : 0 }));
    return { query: header(resolved, ["activity"]), list: "sub-agent-depth", rows, total, totalRows: rows.length, truncated: false };
  }

  // The tiles of the summary.
  function tile(resolved: Resolved, id: string, metric: Metric, unit = metric.unit): SummaryTile {
    const lines: Lines = new Map();
    accumulate(resolved.plan, resolved, metric, null, lines, false);
    if (resolved.compare) accumulate(resolved.compare, resolved, metric, null, lines, true);
    const line = lines.get("");
    const spark = line ? line.values.map(round) : new Array(resolved.plan.buckets.length).fill(0);
    const value = round(sum(spark));
    const previous = resolved.compare ? round(line?.previous ? sum(line.previous) : 0) : undefined;
    return { id, unit, value, ...(previous !== undefined ? { previous } : {}), ...(previous !== undefined && previous !== 0 ? { change: (value - previous) / previous } : {}), spark };
  }

  // A ranking of entities, with the values every table shows.
  type Entity = { key: string; label: string; detail?: string; tokens: number; timeMs: number; calls: number; requests: number; failures: number; spark: number[]; bytes: number };
  function entities(resolved: Resolved, kind: string, by: string): Entity[] {
    const map = new Map<string, Entity>();
    const count = resolved.plan.buckets.length;
    const entity = (key: string, label: string, detail?: string) => {
      let value = map.get(key);
      if (!value) { value = { key, label, detail, tokens: 0, timeMs: 0, calls: 0, requests: 0, failures: 0, spark: new Array(count).fill(0), bytes: 0 }; map.set(key, value); }
      return value;
    };
    const spark = (item: Entity, at: number, cell: Cell, tool?: ToolCell) => { item.spark[at] += by === "time" ? (tool ? tool.timeMs : cell.activeMs) : by === "calls" ? (tool ? tool.calls : cell.tools.reduce((n, t) => n + t.calls, 0)) : cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output; };
    if (kind === "tools") {
      for (const cell of cellsOf(resolved, "tools")) {
        const at = resolved.plan.indexOf(cell.day, cell.hour);
        if (at < 0) continue;
        for (const tool of cell.tools) {
          if (resolved.request.filter?.toolKind && tool.kind !== resolved.request.filter.toolKind) continue;
          const item = entity(tool.tool, tool.tool, tool.kind);
          item.calls += tool.calls; item.failures += tool.failures; item.timeMs += tool.timeMs; item.bytes += tool.bytesIn + tool.bytesOut;
          item.spark[at] += by === "time" ? tool.timeMs : tool.calls;
        }
      }
    } else {
      for (const cell of cellsOf(resolved, "usage")) {
        const at = resolved.plan.indexOf(cell.day, cell.hour);
        if (at < 0) continue;
        const item = kind === "models" ? entity(`${cell.provider}/${cell.model}`, cell.model, cell.provider) : kind === "projects" ? entity(cell.project, projectName(cell.project))
          : entity(cell.session, data.sessions.find(s => s.id === cell.session)?.title ?? cell.session, projectName(cell.project));
        item.tokens += cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output; item.timeMs += cell.activeMs; item.requests += cell.requests;
        item.calls += cell.tools.reduce((n, t) => n + t.calls, 0);
        spark(item, at, cell);
      }
    }
    return [...map.values()];
  }
  const measureOf = (item: Entity, by: string) => by === "time" ? item.timeMs : by === "calls" ? item.calls : item.tokens;

  const costsOf = (cells: readonly Cell[]): CostAmount[] => {
    const totals = new Map<string, number>();
    for (const cell of cells) if (cell.costUnit) totals.set(cell.costUnit, (totals.get(cell.costUnit) ?? 0) + cell.cost);
    return [...totals].map(([unit, amount]) => ({ unit, total: round(amount) })).sort((a, b) => a.unit.localeCompare(b.unit));
  };
  const sparkOf = (resolved: Resolved, cells: readonly Cell[], pick: (cell: Cell) => number): number[] => {
    const values = new Array(resolved.plan.buckets.length).fill(0);
    for (const cell of cells) { const at = resolved.plan.indexOf(cell.day, cell.hour); if (at >= 0) values[at] += pick(cell); }
    return values.map(round);
  };

  function toolRows(resolved: Resolved): { rows: ToolRow[]; kinds: Map<string, number> } {
    const byTool = new Map<string, ToolCell[]>();
    const cells = cellsOf(resolved, "tools");
    const cellOfTool = new Map<string, Cell[]>();
    for (const cell of cells) for (const tool of cell.tools) {
      if (resolved.request.filter?.toolKind && tool.kind !== resolved.request.filter.toolKind) continue;
      (byTool.get(tool.tool) ?? byTool.set(tool.tool, []).get(tool.tool)!).push(tool);
      (cellOfTool.get(tool.tool) ?? cellOfTool.set(tool.tool, []).get(tool.tool)!).push(cell);
    }
    const kinds = new Map<string, number>();
    const rows = [...byTool].map(([name, list]): ToolRow => {
      const definition = fixtureTools.find(item => item.tool === name)!;
      const calls = sum(list.map(item => item.calls)), failures = sum(list.map(item => item.failures));
      const spark = new Array(resolved.plan.buckets.length).fill(0);
      list.forEach((item, index) => { const cell = cellOfTool.get(name)![index]; const at = resolved.plan.indexOf(cell.day, cell.hour); if (at >= 0) spark[at] += item.calls; });
      kinds.set(definition.kind, (kinds.get(definition.kind) ?? 0) + calls);
      return { kind: definition.kind, tool: name, calls, failures, failureRate: calls ? failures / calls : 0, timeMs: sum(list.map(item => item.timeMs)), p50Ms: definition.median, p90Ms: Math.round(definition.median * Math.exp(1.2816 * definition.spread)),
        maxMs: Math.max(...list.map(item => item.maxMs)), bytesIn: sum(list.map(item => item.bytesIn)), bytesOut: sum(list.map(item => item.bytesOut)), spark };
    }).sort((a, b) => b.calls - a.calls);
    return { rows, kinds };
  }

  // A log-normal distribution put in the fixed steps of the plugin (each about 19% wider than the one before).
  const stepLower = (index: number) => Math.round(1.19 ** index);
  function lognormalSteps(count: number, median: number, spread: number): DistributionStep[] {
    if (count <= 0) return [];
    const cdf = (x: number) => 0.5 * (1 + erf((Math.log(Math.max(x, 1e-9)) - Math.log(median)) / (spread * Math.SQRT2)));
    const steps: DistributionStep[] = [];
    for (let index = 0; index < 110; index++) {
      const lower = stepLower(index), upper = stepLower(index + 1);
      if (upper <= lower) continue;
      const amount = Math.round(count * (cdf(upper) - cdf(lower)));
      if (amount > 0) steps.push({ lower, ...(index === 109 ? {} : { upper }), count: amount });
    }
    return steps;
  }
  function erf(x: number): number {
    const sign = x < 0 ? -1 : 1, t = 1 / (1 + 0.3275911 * Math.abs(x));
    const y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.exp(-x * x);
    return sign * y;
  }
  const percentileOfSteps = (steps: readonly DistributionStep[], p: number): number | undefined => {
    const count = sum(steps.map(step => step.count));
    if (count === 0) return undefined;
    let seen = 0;
    for (const step of steps) {
      if (seen + step.count >= count * p) { const upper = step.upper ?? step.lower * 1.19; return Math.round(step.lower + (upper - step.lower) * ((count * p - seen) / step.count)); }
      seen += step.count;
    }
    return undefined;
  };
  function stepsOfValues(values: readonly number[]): DistributionStep[] {
    const counts = new Map<number, number>();
    for (const value of values) { let index = 0; while (index < 109 && stepLower(index + 1) <= value) index++; counts.set(index, (counts.get(index) ?? 0) + 1); }
    return [...counts].sort((a, b) => a[0] - b[0]).map(([index, count]) => ({ lower: stepLower(index), ...(index === 109 ? {} : { upper: stepLower(index + 1) }), count }));
  }

  const iso = (day: string, hour: number, minute = 0) => `${day}T${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}:00Z`;
  const sessionById = (id: string) => data.sessions.find(item => item.id === id);

  function sessionEntries(resolved: Resolved, onlyIds?: readonly string[]): SessionEntry[] {
    const rows = new Map<string, { cells: Cell[] }>();
    for (const cell of cellsOf(resolved, "activity")) (rows.get(cell.session) ?? rows.set(cell.session, { cells: [] }).get(cell.session)!).cells.push(cell);
    const entries: SessionEntry[] = [];
    for (const [id, { cells }] of rows) {
      if (onlyIds && !onlyIds.includes(id)) continue;
      const session = sessionById(id)!;
      const last = cells[cells.length - 1];
      entries.push({
        sessionId: id, title: session.title, project: session.project, projectName: projectName(session.project), provider: session.provider, model: session.model, ...(session.parent ? { parentSessionId: session.parent } : {}),
        runs: sum(cells.map(cell => cell.runs)), activeMs: sum(cells.map(cell => cell.activeMs)), tokens: sum(cells.map(cell => cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output)),
        toolCalls: sum(cells.map(cell => sum(cell.tools.map(tool => tool.calls)))), costs: costsOf(cells), subAgents: data.sessions.filter(item => item.parent === id).length,
        lastActivity: iso(last.day, last.hour, 40), deleted: session.deleted,
      });
    }
    return entries;
  }

  const runSamples = (resolved: Resolved): RunSample[] => {
    const rows: RunSample[] = [];
    for (const cell of cellsOf(resolved, "activity")) {
      cell.runDurations.forEach((duration, index) => {
        const requests = Math.max(1, Math.round(cell.requests / cell.runs));
        rows.push({
          sessionId: cell.session, runId: `run-${cell.session.slice(0, 4)}-${cell.day.slice(5)}-${cell.hour}-${index}`, start: iso(cell.day, cell.hour, index * 9), durationMs: duration,
          outcome: index === 0 && cell.interrupted ? "interrupted" : index === 1 && cell.failed ? "failed" : "completed", origin: cell.origin, promptKind: index === 0 ? "newturn" : "queued",
          promptChars: Math.round(cell.promptChars / Math.max(1, cell.promptsYou)), promptWords: Math.round(cell.promptWords / Math.max(1, cell.promptsYou)), requests,
          toolCalls: Math.round(sum(cell.tools.map(tool => tool.calls)) / cell.runs), toolFailures: Math.round(sum(cell.tools.map(tool => tool.failures)) / cell.runs),
          inputTokens: Math.round((cell.fresh + cell.cacheRead + cell.cacheWrite) / cell.runs), outputTokens: Math.round(cell.output / cell.runs), answerChars: Math.round(cell.output * 0.24 / cell.runs),
          answerWords: Math.round(cell.output * 0.04 / cell.runs), provider: cell.provider, model: cell.model, effort: cell.effort,
        });
      });
    }
    return rows;
  };

  const modelRows = (resolved: Resolved): ModelRow[] => {
    const groups = new Map<string, Cell[]>();
    for (const cell of cellsOf(resolved, "usage")) (groups.get(`${cell.provider}/${cell.model}`) ?? groups.set(`${cell.provider}/${cell.model}`, []).get(`${cell.provider}/${cell.model}`)!).push(cell);
    return [...groups].map(([, cells]): ModelRow => {
      const input = sum(cells.map(cell => cell.fresh + cell.cacheRead + cell.cacheWrite)), cacheRead = sum(cells.map(cell => cell.cacheRead));
      const fills = cells.map(cell => cell.contextFill);
      return { provider: cells[0].provider, model: cells[0].model, requests: sum(cells.map(cell => cell.requests)), inputTokens: input, freshInputTokens: sum(cells.map(cell => cell.fresh)), cacheReadTokens: cacheRead,
        cacheWriteTokens: sum(cells.map(cell => cell.cacheWrite)), outputTokens: sum(cells.map(cell => cell.output)), reasoningTokens: sum(cells.map(cell => cell.reasoning)), cacheShare: input ? cacheRead / input : 0,
        activeMs: sum(cells.map(cell => cell.activeMs)), averageContextFill: fills.length ? sum(fills) / fills.length : undefined, highestContextFill: Math.max(0, ...fills), costs: costsOf(cells),
        spark: sparkOf(resolved, cells, cell => cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output) };
    }).sort((a, b) => (b.inputTokens + b.outputTokens) - (a.inputTokens + a.outputTokens));
  };

  async function guard<T>(method: string, signal: AbortSignal | undefined, produce: () => T): Promise<T> {
    if (signal?.aborted) throw aborted();
    if (latency > 0) {
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(resolve, latency);
        signal?.addEventListener("abort", () => { clearTimeout(timer); reject(aborted()); }, { once: true });
      });
    } else await Promise.resolve();
    if (signal?.aborted) throw aborted();
    const failure = failures.find(item => (item.method === "*" || item.method === method) && item.count > 0);
    if (failure) { failure.count--; throw new Error(failure.message); }
    return produce();
  }

  const api: StatisticsApi = {
    summary: (request, signal) => guard("summary", signal, (): SummaryResult => {
      const resolved = resolve(request);
      const count = resolved.plan.buckets.length;
      const active = sessionsActive(resolved, false, null);
      const spark = active.series[0]?.values ?? new Array(count).fill(0);
      const distinct = new Set(cellsOf(resolved, "activity").map(cell => cell.session)).size;
      const previousDistinct = resolved.compare ? new Set(cellsOf(resolved, "activity", resolved.compare).map(cell => cell.session)).size : undefined;
      const sessionTile: SummaryTile = { id: "sessions", unit: "count", value: distinct, ...(previousDistinct !== undefined ? { previous: previousDistinct } : {}), ...(previousDistinct ? { change: (distinct - previousDistinct) / previousDistinct } : {}), spark };
      const ids = ["runs", "active-time", "your-prompts", "requests", "tokens", "input-tokens", "output-tokens", "fresh-input-tokens", "cache-read-tokens", "cache-write-tokens", "reasoning-tokens",
        "tool-calls", "tool-failures", "lines-added", "lines-removed", "files-changed", "errors", "compactions"];
      const tiles = [sessionTile, ...ids.map(id => tile(resolved, id, metrics[id]))];
      const costs: SummaryTile[] = [];
      const lines: Lines = new Map();
      accumulate(resolved.plan, resolved, metrics.cost, "unit", lines, false);
      if (resolved.compare) accumulate(resolved.compare, resolved, metrics.cost, "unit", lines, true);
      for (const [unit, line] of [...lines].sort((a, b) => a[0].localeCompare(b[0]))) {
        const value = round(sum(line.values)), previous = line.previous ? round(sum(line.previous)) : undefined;
        costs.push({ id: unit, unit, value, ...(previous !== undefined ? { previous } : {}), ...(previous ? { change: (value - previous) / previous } : {}), spark: line.values.map(round) });
      }
      return { query: header(resolved, ["activity", "usage", "tools", "content"]), buckets: resolved.plan.buckets, tiles, costs };
    }),
    series: (request, metric, group, signal) => guard("series", signal, (): SeriesResult => {
      const resolved = resolve(request);
      if (metric === "sessions-active" || metric === "sessions-started") return sessionsActive(resolved, metric === "sessions-started", group);
      if (metric === "sessions-at-once") return sessionsAtOnce(resolved);
      if (metric === "context-fill") return contextFill(resolved, group);
      const definition = metrics[metric];
      if (!definition) throw new Error(`'${metric}' is not a metric.`);
      if (group === "origin" && definition.family !== "content" && metric !== "runs") throw new Error("Only prompts can be cut by origin.");
      const lines: Lines = new Map();
      accumulate(resolved.plan, resolved, definition, definition.family === "cost" ? "unit" : group, lines, false);
      if (resolved.compare) accumulate(resolved.compare, resolved, definition, definition.family === "cost" ? "unit" : group, lines, true);
      return { query: header(resolved, [definition.family]), metric, unit: definition.unit, ...(group || definition.family === "cost" ? { group: definition.family === "cost" ? "unit" : group! } : {}),
        buckets: resolved.plan.buckets, series: toLines(lines, resolved.limit ?? 20) };
    }),
    top: (request, kind, by, signal) => guard("top", signal, (): TopResult => {
      const resolved = resolve(request);
      const list = entities(resolved, kind, by).sort((a, b) => measureOf(b, by) - measureOf(a, by));
      const totalValue = sum(list.map(item => measureOf(item, by)));
      const limit = resolved.limit ?? 10;
      const rows: RankedRow[] = list.slice(0, limit).map(item => ({ key: item.key, label: item.label, ...(item.detail ? { detail: item.detail } : {}), value: measureOf(item, by), share: totalValue ? measureOf(item, by) / totalValue : 0,
        tokens: item.tokens, timeMs: item.timeMs, calls: item.calls, requests: item.requests, ...(kind === "tools" ? { failures: item.failures } : {}), spark: item.spark.map(round) }));
      return { query: header(resolved, [kind === "tools" ? "tools" : "usage"]), kind, by, unit: by === "time" ? "ms" : by === "calls" ? "count" : "tokens", buckets: resolved.plan.buckets, rows, totalRows: list.length, truncated: list.length > limit };
    }),
    tools: (request, signal) => guard("tools", signal, (): ToolsResult => {
      const resolved = resolve(request);
      const { rows, kinds } = toolRows(resolved);
      const limit = resolved.limit ?? 50;
      return { query: header(resolved, ["tools"]), buckets: resolved.plan.buckets, rows: rows.slice(0, limit), kinds: [...kinds].map(([key, value]) => ({ key, value })).sort((a, b) => b.value - a.value), totalRows: rows.length, truncated: rows.length > limit };
    }),
    models: (request, signal) => guard("models", signal, (): ModelsResult => {
      const resolved = resolve(request);
      const rows = modelRows(resolved);
      const efforts = new Map<string, { provider: string; model: string; effort: string; requests: number; tokens: number; reasoning: number; output: number }>();
      for (const cell of cellsOf(resolved, "usage")) {
        const key = `${cell.provider}/${cell.model}/${cell.effort}`;
        const row = efforts.get(key) ?? { provider: cell.provider, model: cell.model, effort: cell.effort, requests: 0, tokens: 0, reasoning: 0, output: 0 };
        row.requests += cell.requests; row.tokens += cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output; row.reasoning += cell.reasoning; row.output += cell.output;
        efforts.set(key, row);
      }
      return { query: header(resolved, ["usage"]), buckets: resolved.plan.buckets, rows, efforts: [...efforts.values()].sort((a, b) => b.tokens - a.tokens).map(row => ({ provider: row.provider, model: row.model, effort: row.effort, requests: row.requests,
        tokens: row.tokens, reasoningShare: row.output ? row.reasoning / row.output : 0 })), totalRows: rows.length, truncated: false };
    }),
    projects: (request, signal) => guard("projects", signal, (): ProjectsResult => {
      const resolved = resolve(request);
      const groups = new Map<string, Cell[]>();
      for (const cell of cellsOf(resolved, "activity")) (groups.get(cell.project) ?? groups.set(cell.project, []).get(cell.project)!).push(cell);
      const rows = [...groups].map(([id, cells]): ProjectRow => ({ project: id, name: projectName(id), sessions: new Set(cells.map(cell => cell.session)).size, runs: sum(cells.map(cell => cell.runs)), activeMs: sum(cells.map(cell => cell.activeMs)),
        tokens: sum(cells.map(cell => cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output)), toolCalls: sum(cells.map(cell => sum(cell.tools.map(tool => tool.calls)))), costs: costsOf(cells), spark: sparkOf(resolved, cells, cell => cell.activeMs) }))
        .sort((a, b) => b.activeMs - a.activeMs);
      return { query: header(resolved, ["activity"]), buckets: resolved.plan.buckets, rows, totalRows: rows.length, truncated: false };
    }),
    sessions: (request, sort, signal) => guard("sessions", signal, (): SessionsResult => {
      const resolved = resolve(request);
      const key = (entry: SessionEntry) => sort === "time" ? entry.activeMs : sort === "tokens" ? entry.tokens : sort === "calls" ? entry.toolCalls : sort === "runs" ? entry.runs : Date.parse(entry.lastActivity ?? "");
      const rows = sessionEntries(resolved).sort((a, b) => key(b) - key(a));
      const limit = resolved.limit ?? 50;
      return { query: header(resolved, ["activity"]), sort, rows: rows.slice(0, limit), totalRows: rows.length, truncated: rows.length > limit };
    }),
    session: (id, withChildren, signal) => guard("session", signal, (): SessionDetailResult => {
      const session = data.sessions.find(item => item.id === id || item.id.startsWith(id));
      if (!session) throw new Error(`No session starts with '${id}'.`);
      const resolved = resolve({ period: `${data.firstDay}..${today}` });
      const ids = [session.id, ...(withChildren ? data.sessions.filter(item => item.parent === session.id).map(item => item.id) : [])];
      const [own] = sessionEntries(resolved, [session.id]);
      const cells = data.cells.filter(cell => ids.includes(cell.session));
      const sub = { ...resolved, request: { ...resolved.request, filter: undefined } };
      const sample = runSamples({ ...sub, plan: resolved.plan }).filter(run => ids.includes(run.sessionId)).sort((a, b) => b.start.localeCompare(a.start)).slice(0, 50);
      const totals: SummaryTile[] = [
        { id: "runs", unit: "count", value: sum(cells.map(cell => cell.runs)), spark: [] }, { id: "active-time", unit: "ms", value: sum(cells.map(cell => cell.activeMs)), spark: [] },
        { id: "tokens", unit: "tokens", value: sum(cells.map(cell => cell.fresh + cell.cacheRead + cell.cacheWrite + cell.output)), spark: [] }, { id: "tool-calls", unit: "count", value: sum(cells.map(cell => sum(cell.tools.map(tool => tool.calls)))), spark: [] },
      ];
      const models = modelRows({ ...resolved, request: { period: resolved.request.period } }).filter(row => cells.some(cell => cell.model === row.model && cell.provider === row.provider));
      const toolsOf = toolRows({ ...resolved, request: { period: resolved.request.period } }).rows.slice(0, 10);
      return { query: header(resolved, ["activity"]), session: own ?? { sessionId: session.id, title: session.title, runs: 0, activeMs: 0, tokens: 0, toolCalls: 0, costs: [], subAgents: 0, deleted: session.deleted },
        children: withChildren ? sessionEntries(resolved, ids.slice(1)) : [], totals, runs: sample.map(run => ({ runId: run.runId, start: run.start, durationMs: run.durationMs, outcome: run.outcome, sender: run.origin, requests: run.requests,
          toolCalls: run.toolCalls, inputTokens: run.inputTokens, outputTokens: run.outputTokens, model: run.model })), models, tools: toolsOf };
    }),
    distribution: (request, measure, subject, signal) => guard("distribution", signal, (): DistributionResult => {
      const resolved = resolve(request);
      const cells = cellsOf(resolved, "activity");
      let steps: DistributionStep[] = [], unit = "count";
      switch (measure) {
        case "run-duration": steps = stepsOfValues(cells.flatMap(cell => cell.runDurations)); unit = "ms"; break;
        case "tool-duration": {
          const definition = fixtureTools.find(item => item.tool === subject);
          const calls = sum(cellsOf(resolved, "tools").flatMap(cell => cell.tools.filter(tool => !subject || tool.tool === subject).map(tool => tool.calls)));
          steps = definition ? lognormalSteps(calls, definition.median, definition.spread) : lognormalSteps(calls, 300, 1.4); unit = "ms"; break;
        }
        case "request-input": steps = lognormalSteps(sum(cells.map(cell => cell.requests)), 42_000, 0.9); unit = "tokens"; break;
        case "request-output": steps = lognormalSteps(sum(cells.map(cell => cell.requests)), 600, 0.8); unit = "tokens"; break;
        case "prompt-chars": steps = lognormalSteps(sum(cells.map(cell => cell.promptsYou)), 250, 1.1); unit = "chars"; break;
        case "prompt-words": steps = stepsOfValues(cells.filter(cell => cell.promptsYou > 0).map(cell => Math.max(1, Math.round(cell.promptWords / cell.promptsYou)))); unit = "words"; break;
        case "run-cost": steps = lognormalSteps(cells.filter(cell => cell.costUnit && (!subject || cell.costUnit === subject)).length * 3, 700_000, 0.9); unit = "micro-unit"; break;
        case "run-tool-calls": steps = stepsOfValues(cells.map(cell => Math.max(1, Math.round(sum(cell.tools.map(tool => tool.calls)) / cell.runs)))); break;
        default: throw new Error(`'${measure}' is not a distribution.`);
      }
      const count = sum(steps.map(step => step.count));
      return { query: header(resolved, ["activity"]), measure, ...(subject ? { subject } : {}), unit, count, ...(count ? { p50: percentileOfSteps(steps, 0.5), p90: percentileOfSteps(steps, 0.9) } : {}), steps };
    }),
    calendar: (request, signal) => guard("calendar", signal, (): CalendarResult => {
      const resolved = resolve({ ...request, frequency: "day" });
      const byDay = new Map<string, { activeMs: number; runs: number; prompts: number }>();
      for (const cell of cellsOf(resolved, "activity")) {
        const row = byDay.get(cell.day) ?? { activeMs: 0, runs: 0, prompts: 0 };
        row.activeMs += cell.activeMs; row.runs += cell.runs; row.prompts += cell.promptsYou; byDay.set(cell.day, row);
      }
      const days = [...byDay].sort((a, b) => a[0].localeCompare(b[0])).map(([date, row]) => ({ date, ...row }));
      return { query: header(resolved, ["activity"]), days, maxActiveMs: Math.max(0, ...days.map(day => day.activeMs)) };
    }),
    weekHour: (request, signal) => guard("weekHour", signal, (): WeekHourResult => {
      const resolved = resolve(request);
      const matrix = () => Array.from({ length: 7 }, () => new Array(24).fill(0) as number[]);
      const active = matrix(), runs = matrix();
      for (const cell of cellsOf(resolved, "activity")) { const row = (dayOfWeek(cell.day) - resolved.weekStart + 7) % 7; active[row][cell.hour] += cell.activeMs; runs[row][cell.hour] += cell.runs; }
      const names = Array.from({ length: 7 }, (_, row) => ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"][(resolved.weekStart + row) % 7]);
      return { query: header(resolved, ["activity"]), weekdays: names, activeMs: active, runs };
    }),
    records: (request, signal) => guard("records", signal, (): RecordsResult => {
      const resolved = resolve(request);
      const cells = cellsOf(resolved, "activity");
      const records: RecordEntry[] = [];
      if (cells.length === 0) return { query: header(resolved, ["activity"]), records };
      let longest = cells[0], longestIndex = 0;
      for (const cell of cells) cell.runDurations.forEach((value, index) => { if (value > longest.runDurations[longestIndex]) { longest = cell; longestIndex = index; } });
      records.push({ measure: "longestRun", subject: "", value: longest.runDurations[longestIndex], unit: "ms", sessionId: longest.session, runId: `run-${longest.session.slice(0, 4)}-${longest.day.slice(5)}-${longest.hour}-${longestIndex}`, at: iso(longest.day, longest.hour) });
      const busiest = cells.reduce((best, cell) => cell.runs * 0 + sum(cell.tools.map(tool => tool.calls)) / cell.runs > sum(best.tools.map(tool => tool.calls)) / best.runs ? cell : best, cells[0]);
      records.push({ measure: "mostToolCallsInRun", subject: "", value: Math.round(sum(busiest.tools.map(tool => tool.calls)) / busiest.runs), unit: "count", sessionId: busiest.session, at: iso(busiest.day, busiest.hour) });
      const perDay = new Map<string, number>();
      for (const cell of cells) perDay.set(cell.day, (perDay.get(cell.day) ?? 0) + cell.activeMs);
      const [busyDay, busyMs] = [...perDay].sort((a, b) => b[1] - a[1])[0];
      records.push({ measure: "busiestDay", subject: "", value: busyMs, unit: "ms", at: busyDay });
      const days = [...perDay.keys()].sort();
      let best = 0, current = 0, previous = "";
      for (const day of days) { current = previous && dayDistance(previous, day) === 1 ? current + 1 : 1; best = Math.max(best, current); previous = day; }
      records.push({ measure: "longestStreak", subject: "", value: best, unit: "days", at: days[days.length - 1] });
      const prompt = cells.reduce((top, cell) => cell.promptWords / Math.max(1, cell.promptsYou) > top.promptWords / Math.max(1, top.promptsYou) ? cell : top, cells[0]);
      records.push({ measure: "largestPrompt", subject: "", value: Math.round(prompt.promptChars / Math.max(1, prompt.promptsYou)), unit: "chars", sessionId: prompt.session, at: iso(prompt.day, prompt.hour) });
      const tool = cellsOf(resolved, "tools").flatMap(cell => cell.tools.map(item => ({ cell, item }))).sort((a, b) => b.item.maxMs - a.item.maxMs)[0];
      if (tool) records.push({ measure: "longestTool", subject: tool.item.tool, value: tool.item.maxMs, unit: "ms", sessionId: tool.cell.session, at: iso(tool.cell.day, tool.cell.hour) });
      return { query: header(resolved, ["activity"]), records };
    }),
    health: (request, signal) => guard("health", signal, (): HealthResult => {
      const resolved = resolve(request);
      const errors = new Array(resolved.plan.buckets.length).fill(0), interrupted = [...errors], compactions = [...errors];
      let runs = 0, before = 0, after = 0;
      for (const cell of cellsOf(resolved, "activity")) {
        const at = resolved.plan.indexOf(cell.day, cell.hour);
        if (at < 0) continue;
        errors[at] += cell.errors; interrupted[at] += cell.interrupted; compactions[at] += cell.compactions; runs += cell.runs;
        before += cell.compactions * 172_000; after += cell.compactions * 58_000;
      }
      const totalErrors = sum(errors), totalCompactions = sum(compactions);
      const failed = toolRows(resolved).rows.filter(row => row.failures > 0).sort((a, b) => b.failures - a.failures).slice(0, 8);
      const models = modelRows(resolved);
      return { query: header(resolved, ["activity", "tools"]), buckets: resolved.plan.buckets, errors, interruptedRuns: interrupted, runs, errorRate: runs ? totalErrors / runs : 0, failedTools: failed,
        compactionsByTrigger: [{ key: "threshold", value: Math.round(totalCompactions * 0.62) }, { key: "overflow", value: Math.round(totalCompactions * 0.23) }, { key: "manual", value: Math.round(totalCompactions * 0.15) }].filter(item => item.value > 0),
        compactions, tokensBeforeCompaction: before, tokensAfterCompaction: after,
        contextByModel: models.map(row => ({ provider: row.provider, model: row.model, ...(row.averageContextFill !== undefined ? { average: row.averageContextFill } : {}), highest: row.highestContextFill, samples: row.requests })) };
    }),
    details: (request, list, signal) => guard("details", signal, (): DetailsResult => {
      const resolved = resolve(request);
      if (list === "sub-agent-depth") return subAgentDepth(resolved);
      const toolCalls = (name: string) => sum(cellsOf(resolved, "tools").flatMap(cell => cell.tools.filter(tool => tool.tool === name).map(tool => tool.calls)));
      const shares: Record<string, [number, readonly (readonly [string, number])[]]> = {
        "shell-program": [toolCalls("shell"), [["git", 0.34], ["dotnet", 0.22], ["npm", 0.12], ["node", 0.08], ["rg", 0.07], ["ls", 0.05], ["curl", 0.04], ["pwsh", 0.03], ["cat", 0.03], ["gh", 0.02]]],
        "alta-command": [toolCalls("ToolCall:alta"), [["session create", 0.3], ["session send", 0.22], ["task create", 0.14], ["notes set", 0.12], ["reminder create", 0.1], ["plan status", 0.07], ["project list", 0.05]]],
        "changed-file-extension": [toolCalls("ToolCall:apply_patch") + toolCalls("ToolCall:write_file"), [[".cs", 0.38], [".tsx", 0.17], [".ts", 0.15], [".md", 0.12], [".css", 0.07], [".json", 0.06], [".csproj", 0.03], [".toml", 0.02]]],
        skill: [toolCalls("Skill:alta"), [["codealta-plugin-runtime", 0.5], ["ilspy-decompile", 0.3], ["dataviz", 0.2]]],
        "permission-mode": [sum(cellsOf(resolved, "activity").map(cell => cell.runs)), [["ask", 0.5], ["accept edits", 0.38], ["bypass", 0.12]]],
        "compaction-trigger": [sum(cellsOf(resolved, "activity").map(cell => cell.compactions)), [["threshold", 0.62], ["overflow", 0.23], ["manual", 0.15]]],
        "run-origin": [sum(cellsOf(resolved, "activity").map(cell => cell.runs)), [["you", 0.8], ["agent", 0.12], ["automation", 0.05], ["reminder", 0.03]]],
        "session-origin": [new Set(cellsOf(resolved, "activity").map(cell => cell.session)).size, [["you", 0.78], ["agent", 0.17], ["automation", 0.03], ["reminder", 0.02]]],
      };
      const entry = shares[list];
      if (!entry) throw new Error(`'${list}' is not a list.`);
      const rows = entry[1].map(([name, share]) => ({ name, count: Math.round(entry[0] * share), share })).filter(row => row.count > 0);
      const totalCount = sum(rows.map(row => row.count));
      const limit = resolved.limit ?? 50;
      return { query: header(resolved, ["activity", "tools"]), list, rows: rows.slice(0, limit), total: totalCount, totalRows: rows.length, truncated: rows.length > limit };
    }),
    runs: (request, sort, signal) => guard("runs", signal, (): RunsResult => {
      const resolved = resolve(request);
      const all = runSamples(resolved);
      const key = (run: RunSample) => sort === "longest" ? run.durationMs : sort === "tokens" ? run.inputTokens + run.outputTokens : sort === "tools" ? run.toolCalls : Date.parse(run.start);
      const sorted = all.sort((a, b) => key(b) - key(a));
      const limit = resolved.limit ?? 50;
      return { query: header(resolved, ["activity"]), sort, runs: sorted.slice(0, limit), totalRows: sorted.length, truncated: sorted.length > limit };
    }),

    status: signal => guard("status", signal, () => status),
    chooseHistory: async (choice: HistoryChoice) => {
      const days = choice.kind === "days" ? choice.days : 0;
      if (choice.kind === "fromToday") setStatus({ state: "done", choice: "from-today", floorDay: dayNumber(today), sessionsTotal: 0, sessionsDone: 0, bytesTotal: 0, bytesDone: 0, completeFromDay: undefined, isComplete: true });
      else {
        const floor = choice.kind === "days" ? dayNumber(addDays(today, 1 - days)) : undefined;
        const share = choice.kind === "days" ? data.sessions.filter(session => session.last >= addDays(today, 1 - days)).length / data.sessions.length : 1;
        setStatus({ state: "reading", reason: status.state === "needsChoice" ? "first-read" : "extended", choice: choice.kind === "all" ? "all" : `days:${days}`, floorDay: floor, sessionsTotal: Math.max(1, Math.round(total.sessions * share)),
          sessionsDone: 0, bytesTotal: Math.round(total.bytes * share), bytesDone: 0, completeFromDay: dayNumber(today), oldestDateReached: dayNumber(today), bytesPerSecond: 480_000_000, etaSeconds: Math.round(total.bytes * share / 480_000_000), isComplete: false });
      }
      return status;
    },
    pause: async () => { setStatus({ state: "paused" }); return status; },
    // As the plugin does: a start that failed is tried again (and works), the sessions that could not be read are tried again
    // (and are read), and a paused reading goes on.
    resume: async () => {
      if (status.state === "failed") { status = { ...scenarios["first-time"](), revision }; emit({ kind: "status", status }); }
      else if ((status.state === "done" || status.state === "stoppedHere") && status.skippedCount > 0) setStatus({ skippedCount: 0, skipped: [] });
      else if (status.state === "paused") setStatus({ state: "reading" });
      return status;
    },
    stopHere: async () => { setStatus({ state: "stoppedHere", floorDay: status.completeFromDay, isComplete: false }); return status; },
    forgetDeleted: async () => { const count = data.sessions.filter(session => session.deleted).length; emit({ kind: "data", change: { revision: ++revision, fromDay: firstDayNumber, toDay: dayNumber(today), sessionIds: [] } }); return count; },
    resetStatistics: async () => { status = { ...scenarios["first-time"](), revision }; emit({ kind: "status", status }); emit({ kind: "data", change: { revision: ++revision, fromDay: firstDayNumber, toDay: dayNumber(today), sessionIds: [] } }); return status; },
    subscribe: listener => { listeners.add(listener); return () => { listeners.delete(listener); }; },
  };
  if (options.estimates) {
    (api as { costEstimate?: StatisticsApi["costEstimate"] }).costEstimate = (request, signal) => guard("costEstimate", signal, (): SeriesResult => {
      const resolved = resolve(request);
      const lines: Lines = new Map();
      accumulate(resolved.plan, resolved, { family: "usage", unit: "usd", value: cell => (cell.fresh * 3 + cell.cacheRead * 0.3 + cell.cacheWrite * 3.75 + cell.output * 15) / 1_000_000 }, "model", lines, false);
      return { query: header(resolved, ["usage"]), metric: "cost-estimate", unit: "usd", group: "model", buckets: resolved.plan.buckets, series: toLines(lines, resolved.limit ?? 20) };
    });
  }

  const control: FixtureControl = {
    data, status: () => status, setStatus,
    advance: (sessions = 100) => {
      if (status.state !== "reading") return;
      const done = Math.min(status.sessionsTotal, status.sessionsDone + sessions);
      const fraction = status.sessionsTotal ? done / status.sessionsTotal : 1;
      if (done >= status.sessionsTotal) {
        setStatus({ state: "done", sessionsDone: status.sessionsTotal, bytesDone: status.bytesTotal, completeFromDay: undefined, etaSeconds: undefined, oldestDateReached: status.floorDay ?? firstDayNumber, isComplete: true });
      } else {
        const spanDays = dayDistance(status.floorDay ? dayOfNumber(status.floorDay) : data.firstDay, today);
        const reached = dayNumber(addDays(today, -Math.round(spanDays * fraction)));
        setStatus({ sessionsDone: done, bytesDone: Math.round(status.bytesTotal * fraction), oldestDateReached: reached, completeFromDay: reached, etaSeconds: Math.round((1 - fraction) * 120) });
      }
      emit({ kind: "data", change: { revision: ++revision, fromDay: status.oldestDateReached ?? firstDayNumber, toDay: dayNumber(today), sessionIds: [] } });
    },
    emitData: (fromDay = dayNumber(today), toDay = dayNumber(today)) => emit({ kind: "data", change: { revision: ++revision, fromDay, toDay, sessionIds: [] } }),
    failNext: (method, message = "The plugin could not answer.", count = 1) => { failures.push({ method, message, count }); },
    setLatency: milliseconds => { latency = milliseconds; },
    listenerCount: () => listeners.size,
  };
  return Object.assign(api, { control }) as FixtureApi;
}
