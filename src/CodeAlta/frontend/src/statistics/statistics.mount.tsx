// The Statistics canvas alone, in a page, over the fixture API: no application host. Driven by statistics.browser.test.ts through
// `window.statsFixture`; the canvas is mounted under React StrictMode, as the application is, and every question it asks is recorded.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot, type Root } from "react-dom/client";
import { ShellLanguageContext } from "../shellLanguage";
import type { FleetStatisticsGroupView, FleetStatisticsMeasuresView, FleetStatisticsView } from "#neoastra";
import type { StatisticsApi, StatisticsContext } from "./api";
import type { FleetQuery, FleetStatisticsApi } from "./fleet";
import { createFixtureApi, type FixtureApi, type FixtureScenario } from "./fixtureApi";
import { StatisticsCanvas } from "./StatisticsCanvas";
import type { DetailsResult, SeriesResult } from "./types";
import { installRefreshClock } from "./refreshClock.fixture";

type Call = { method: string; request: unknown; args: unknown[]; signal: AbortSignal | null };
const calls: Call[] = [];
const opened: string[] = [];
const files = new Map<string, string>();
const storage = { getItem: (key: string) => files.get(key) ?? null, setItem: (key: string, value: string) => { files.set(key, value); }, removeItem: (key: string) => { files.delete(key); } };
const nothing = () => { };
const advance = installRefreshClock();

const questions = ["summary", "series", "top", "tools", "models", "projects", "sessions", "session", "distribution", "calendar", "weekHour", "records", "health", "details", "skills", "mcpTools", "runs", "scripts", "costEstimate"] as const;
const controls = ["status", "chooseHistory", "pause", "resume", "stopHere", "forgetDeleted", "resetStatistics"] as const;

function recording(api: FixtureApi, options: Options): StatisticsApi {
  const wrapped: Record<string, unknown> = { subscribe: api.subscribe };
  for (const name of [...questions, ...controls]) {
    const original = (api as unknown as Record<string, unknown>)[name];
    if (typeof original !== "function") continue;
    wrapped[name] = async (...args: unknown[]) => {
      const signal = args.find((value): value is AbortSignal => typeof AbortSignal !== "undefined" && value instanceof AbortSignal) ?? null;
      const rest = args.filter(value => value !== signal);
      const request = rest.find(value => typeof value === "object" && value !== null && "period" in (value as object)) ?? null;
      calls.push({ method: name, request, args: rest.filter(value => value !== request), signal });
      const result = await (original as (...parameters: unknown[]) => unknown).apply(api, args);
      if (name === "series" && options.contextSamples !== undefined && (args[1] === "context-fill" || args[1] === "context-samples")) {
        const series = result as SeriesResult;
        const value = args[1] === "context-samples" ? options.contextSamples : 0;
        return { ...series, series: series.series.map(line => ({ ...line, total: value * series.buckets.length, values: series.buckets.map(() => value) })) };
      }
      if (options.derivedNotes && ((name === "series" && args[1] === "sessions-at-once") || (name === "details" && args[1] === "sub-agent-depth"))) {
        const derived = result as SeriesResult | DetailsResult;
        return { ...derived, query: { ...derived.query, notes: [...derived.query.notes, name === "series" ? "runs-of-unknown-time-left-out" : "some-parents-unknown"] } };
      }
      return result;
    };
  }
  return wrapped as unknown as StatisticsApi;
}

type Options = {
  scenario?: FixtureScenario; visible?: boolean; width?: number; spaceId?: string | null; instanceId?: string; latencyMs?: number; locale?: string; estimates?: boolean;
  projectId?: string | null; sessionCount?: number;
  /** The session the canvas is limited to, with its sub-agents: the canvas of the menu of a session. */
  sessionId?: string | null;
  /** False while the window has not said how it names its providers: the context has no names yet. */
  providers?: boolean;
  /** How many canvases are drawn side by side over the same fixture: two tabs of the statistics in one window. */
  copies?: number;
  /** Supplies zero fill with this many samples per bucket, distinguishing measured zero from no observation. */
  contextSamples?: number;
  /** Supplies the metric-local incomplete ancestry and run-time notes. */
  derivedNotes?: boolean;
  /** Gives the canvas the scope "My fleet" over a fixture of two machines: `true` linked to a server, `"unlinked"` not. */
  fleet?: boolean | "unlinked";
};

// "My fleet" over a fixture: two machines, a project this computer names and one it does not, two models and the tool calls without one.
const fleetMeasures = (input: number, output: number, activeMs: number, usd: number): FleetStatisticsMeasuresView => ({
  activeMs, runs: Math.round(activeMs / 600_000) + 1, runsFailed: 0, requests: 3, inputTokens: input, outputTokens: output, cacheReadTokens: 0, cacheWriteTokens: 0,
  reasoningTokens: 0, toolCalls: 12, toolFailures: 1, costs: usd ? [{ unit: "usd", amount: usd }] : [],
});
const fleetGroups: Record<string, FleetStatisticsGroupView[]> = {
  machine: [
    { machine: "d-desktop", machineName: "Desktop", project: null, projectName: null, provider: null, model: null, period: null, measures: fleetMeasures(820_000, 64_000, 9_200_000, 12.4) },
    { machine: "d-mini", machineName: "Mac mini", project: null, projectName: null, provider: null, model: null, period: null, measures: fleetMeasures(1_450_000, 98_000, 15_600_000, 21.75) },
  ],
  project: [
    { machine: null, machineName: null, project: "Aq3dF9xYzW_kLm0pQr7sTu", projectName: "CodeAlta", provider: null, model: null, period: null, measures: fleetMeasures(1_900_000, 140_000, 20_000_000, 30) },
    { machine: null, machineName: null, project: "Zz81kQpLmN_aB2cD3eF4gH", projectName: null, provider: null, model: null, period: null, measures: fleetMeasures(370_000, 22_000, 4_800_000, 4.15) },
  ],
  provider: [
    { machine: null, machineName: null, project: null, projectName: null, provider: "anthropic", model: "claude-opus-5-5", period: null, measures: fleetMeasures(1_600_000, 120_000, 17_000_000, 31) },
    { machine: null, machineName: null, project: null, projectName: null, provider: "openai", model: "gpt-6.1", period: null, measures: fleetMeasures(670_000, 42_000, 7_800_000, 3.15) },
    { machine: null, machineName: null, project: null, projectName: null, provider: "anthropic", model: "", period: null, measures: fleetMeasures(0, 0, 0, 0) },
  ],
};

function fleetFixture(linked: boolean): FleetStatisticsApi {
  return {
    available: async () => linked,
    statistics: async (query: FleetQuery, signal?: AbortSignal) => {
      calls.push({ method: "fleet", request: query, args: [], signal: signal ?? null });
      const groups = fleetGroups[query.groupBy[0] ?? "machine"] ?? [];
      const view: FleetStatisticsView = {
        linkId: "link-1", from: query.from, to: query.to, timeZone: query.timeZone ?? "UTC", groupBy: [...query.groupBy], totals: fleetMeasures(2_270_000, 162_000, 24_800_000, 34.15),
        groups, machines: [
          { id: "d-desktop", name: "Desktop", kind: "controller", isThisComputer: true, lastPublishedAt: "2026-10-09T09:12:00Z", sessions: 41 },
          { id: "d-mini", name: "Mac mini", kind: "node", isThisComputer: false, lastPublishedAt: "2026-10-09T09:14:30Z", sessions: 63 },
          { id: "d-laptop", name: "Laptop", kind: "controller", isThisComputer: false, lastPublishedAt: null, sessions: 0 },
        ], truncated: false,
      };
      return { status: "ok", view, message: null };
    },
  };
}

let root: Root | null = null;
let api: FixtureApi | null = null;
let wrapped: StatisticsApi | null = null;
let fleetApi: FleetStatisticsApi | null = null;
let state: Options & { visible: boolean } = { visible: true };

function draw() {
  const container = document.getElementById("root")!;
  root ??= createRoot(container);
  const context: StatisticsContext = {
    instanceId: state.instanceId ?? "canvas-1", visible: state.visible, spaceId: state.spaceId, projectId: state.projectId, sessionId: state.sessionId, today: "2026-10-09",
    spaces: [{ id: "space-work", name: "Work", projectIds: ["proj-codealta", "proj-neoastra"] }, { id: "space-oss", name: "Open source", projectIds: ["proj-xenoatom", "proj-tomlyn", "proj-sharpyaml"] }],
    // The window names three of the five providers of the fixture: the two others are read under their key.
    providers: state.providers === false ? undefined : [{ key: "claude-code", name: "Claude Code" }, { key: "codex", name: "Codex" }, { key: "copilot", name: "GitHub Copilot" }],
    openSession: id => { opened.push(id); }, storage,
  };
  const locale = state.locale ?? "en";
  const fleet = fleetApi;
  flushSync(() => root!.render(createElement(StrictMode, null, createElement(ShellLanguageContext.Provider, { value: { locale: locale as "en", choice: locale as "en", setLanguage: nothing } },
    createElement("div", { id: "stats-host", style: { width: state.width ? `${state.width}px` : "100%", height: "100%" } },
      ...Array.from({ length: state.copies ?? 1 }, (_, index) => {
        const own = index === 0 ? context : { ...context, instanceId: `${context.instanceId}-${index + 1}` };
        return createElement(StatisticsCanvas, { key: own.instanceId, api: wrapped!, context: own, fleet });
      }))))));
}

const fixture = {
  advance,
  calls: () => calls.map(call => ({ method: call.method, request: call.request, args: call.args, aborted: call.signal?.aborted ?? false })),
  clearCalls: () => { calls.length = 0; },
  opened,
  storage: files,
  get control() { return api!.control; },
  /** Mounts the canvas over a new fixture. */
  render(options: Options = {}) {
    state = { visible: true, ...options };
    api = createFixtureApi({ today: "2026-10-09", scenario: options.scenario, latencyMs: options.latencyMs, estimates: options.estimates, sessionCount: options.sessionCount });
    wrapped = recording(api, options);
    fleetApi = options.fleet ? fleetFixture(options.fleet === true) : null;
    draw();
  },
  /** Mounts it again over the same fixture: a reload of the page. */
  remount() { flushSync(() => root?.render(null)); draw(); },
  unmount() { flushSync(() => root?.render(null)); },
  update(change: Partial<Options>) { state = { ...state, ...change, visible: change.visible ?? state.visible }; draw(); },
  setTheme(dark: boolean) { document.documentElement.classList.toggle("bp6-dark", dark); document.documentElement.dataset.theme = dark ? "dark" : "light"; },
};
Object.assign(window, { statsFixture: fixture });
document.documentElement.classList.add("bp6-dark");
document.documentElement.dataset.theme = "dark";
