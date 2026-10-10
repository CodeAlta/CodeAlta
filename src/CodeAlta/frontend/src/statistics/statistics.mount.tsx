// The Statistics canvas alone, in a page, over the fixture API: no application host. Driven by statistics.browser.test.ts through
// `window.statsFixture`; the canvas is mounted under React StrictMode, as the application is, and every question it asks is recorded.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot, type Root } from "react-dom/client";
import { ShellLanguageContext } from "../shellLanguage";
import type { StatisticsApi, StatisticsContext } from "./api";
import { createFixtureApi, type FixtureApi, type FixtureScenario } from "./fixtureApi";
import { StatisticsCanvas } from "./StatisticsCanvas";
import type { DetailsResult, SeriesResult } from "./types";

type Call = { method: string; request: unknown; args: unknown[]; signal: AbortSignal | null };
const calls: Call[] = [];
const opened: string[] = [];
const files = new Map<string, string>();
const storage = { getItem: (key: string) => files.get(key) ?? null, setItem: (key: string, value: string) => { files.set(key, value); }, removeItem: (key: string) => { files.delete(key); } };
const nothing = () => { };

const questions = ["summary", "series", "top", "tools", "models", "projects", "sessions", "session", "distribution", "calendar", "weekHour", "records", "health", "details", "runs", "costEstimate"] as const;
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
};

let root: Root | null = null;
let api: FixtureApi | null = null;
let wrapped: StatisticsApi | null = null;
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
  flushSync(() => root!.render(createElement(StrictMode, null, createElement(ShellLanguageContext.Provider, { value: { locale: locale as "en", choice: locale as "en", setLanguage: nothing } },
    createElement("div", { id: "stats-host", style: { width: state.width ? `${state.width}px` : "100%", height: "100%" } },
      ...Array.from({ length: state.copies ?? 1 }, (_, index) => {
        const own = index === 0 ? context : { ...context, instanceId: `${context.instanceId}-${index + 1}` };
        return createElement(StatisticsCanvas, { key: own.instanceId, api: wrapped!, context: own });
      }))))));
}

const fixture = {
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
