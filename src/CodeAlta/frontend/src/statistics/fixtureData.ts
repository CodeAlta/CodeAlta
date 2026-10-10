import { addDays, dayDistance } from "./frame";

// Generated data for the fixture API: about six months of sessions in several projects, with several providers and
// models, deterministic (the same seed gives the same numbers), shaped like the numbers a person who uses CodeAlta all
// day leaves. It is a list of "cells" (a session in an hour of a day) from which `fixtureApi.ts` answers every question
// by plain addition, the way the plugin's roll-ups do.

/** A project of the fixture. */
export type FixtureProject = Readonly<{ id: string; name: string }>;
/** A space of the fixture: a name and the projects it has. */
export type FixtureSpace = Readonly<{ id: string; name: string; projects: readonly string[] }>;
/** A model of the fixture. */
export type FixtureModel = Readonly<{ provider: string; model: string; efforts: readonly string[]; costUnit: "usd" | "AI credits" | null; contextWindow: number; speed: number }>;

/** What one tool did in one cell. */
export type ToolCell = Readonly<{ tool: string; kind: string; calls: number; failures: number; timeMs: number; maxMs: number; bytesIn: number; bytesOut: number; linesAdded: number; linesRemoved: number; filesChanged: number }>;

/** One session in one hour of one day. */
export type Cell = Readonly<{
  session: string;
  day: string;
  hour: number;
  project: string;
  provider: string;
  model: string;
  effort: string;
  origin: "you" | "agent" | "automation" | "reminder";
  delegated: boolean;
  runs: number;
  completed: number;
  failed: number;
  interrupted: number;
  activeMs: number;
  runDurations: readonly number[];
  promptsYou: number;
  promptsOther: number;
  promptKinds: readonly [number, number, number, number];
  promptChars: number;
  promptWords: number;
  attachments: readonly [number, number, number];
  answers: number;
  requests: number;
  fresh: number;
  cacheRead: number;
  cacheWrite: number;
  output: number;
  reasoning: number;
  errors: number;
  compactions: number;
  contextFill: number;
  costUnit: "usd" | "AI credits" | null;
  cost: number;
  tools: readonly ToolCell[];
}>;

/** A session of the fixture. */
export type FixtureSession = Readonly<{ id: string; title: string; project: string; provider: string; model: string; parent: string | null; origin: Cell["origin"]; first: string; last: string; deleted: boolean }>;

/** The generated data. */
export type FixtureData = Readonly<{
  today: string;
  firstDay: string;
  projects: readonly FixtureProject[];
  spaces: readonly FixtureSpace[];
  models: readonly FixtureModel[];
  sessions: readonly FixtureSession[];
  cells: readonly Cell[];
}>;

/** The tools the fixture knows, keyed as the plugin keys them (the kind of activity and the name, `shell` for every shell tool), with their kind and typical duration (median, in ms) and failure rate. */
export const fixtureTools = [
  { tool: "ToolCall:read_file", kind: "files", median: 40, spread: 0.7, failure: 0.01 },
  { tool: "ToolCall:apply_patch", kind: "files", median: 120, spread: 0.8, failure: 0.04 },
  { tool: "ToolCall:write_file", kind: "files", median: 90, spread: 0.7, failure: 0.01 },
  { tool: "ToolCall:grep", kind: "search", median: 260, spread: 0.9, failure: 0.015 },
  { tool: "ToolCall:list_dir", kind: "search", median: 35, spread: 0.6, failure: 0.005 },
  { tool: "shell", kind: "shell", median: 2400, spread: 1.5, failure: 0.12 },
  { tool: "ToolCall:webget", kind: "web", median: 1300, spread: 1, failure: 0.06 },
  { tool: "ToolCall:alta", kind: "alta", median: 480, spread: 0.9, failure: 0.03 },
  { tool: "ToolCall:mcp__github__issue_read", kind: "mcp", median: 900, spread: 0.8, failure: 0.05 },
  { tool: "Skill:alta", kind: "skill", median: 70, spread: 0.5, failure: 0.0 },
  // The same tool of an MCP server, recorded as another kind of activity: the pages show it as one tool.
  { tool: "McpToolCall:mcp__github__issue_read", kind: "mcp", median: 900, spread: 0.8, failure: 0.05 },
] as const;

export const fixtureProjects: readonly FixtureProject[] = [
  { id: "proj-codealta", name: "CodeAlta" }, { id: "proj-neoastra", name: "NeoAstra" }, { id: "proj-xenoatom", name: "XenoAtom" },
  { id: "proj-tomlyn", name: "Tomlyn" }, { id: "proj-sharpyaml", name: "SharpYaml" }, { id: "proj-notes", name: "Notes" },
];

export const fixtureSpaces: readonly FixtureSpace[] = [
  { id: "space-work", name: "Work", projects: ["proj-codealta", "proj-neoastra"] },
  { id: "space-oss", name: "Open source", projects: ["proj-xenoatom", "proj-tomlyn", "proj-sharpyaml"] },
];

export const fixtureModels: readonly FixtureModel[] = [
  { provider: "claude-code", model: "claude-opus-5-5", efforts: ["medium", "high"], costUnit: "usd", contextWindow: 1_000_000, speed: 1 },
  { provider: "codex", model: "gpt-6.1", efforts: ["low", "medium", "high"], costUnit: null, contextWindow: 400_000, speed: 1.2 },
  { provider: "copilot", model: "claude-sonnet-5-5", efforts: ["medium"], costUnit: "AI credits", contextWindow: 200_000, speed: 0.9 },
  { provider: "gemini", model: "gemini-3-pro", efforts: ["medium", "high"], costUnit: null, contextWindow: 1_000_000, speed: 1.1 },
  { provider: "mistral", model: "devstral-2", efforts: ["medium"], costUnit: null, contextWindow: 128_000, speed: 1.4 },
  // A model that a second provider has too: a row is a model of a provider.
  { provider: "copilot", model: "gpt-6.1", efforts: ["medium"], costUnit: "AI credits", contextWindow: 400_000, speed: 1.1 },
];

const verbs = ["Fix", "Add", "Refactor", "Review", "Explore", "Test", "Document", "Profile", "Investigate", "Plan", "Clean up", "Port"];
const objects = ["quoted keys", "the timeline scroll", "provider sign-in", "the release notes", "session recovery", "a flaky test", "the website build", "color schemes",
  "the plugin runtime", "markdown tables", "the history reader", "terminal resize", "an upgrade of Blueprint", "the settings pages", "tool permissions", "the canvas tab"];

/** A small deterministic random generator. */
export function createRandom(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6D2B79F5) >>> 0;
    let value = state;
    value = Math.imul(value ^ (value >>> 15), value | 1);
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61);
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
  };
}

/** A value from a log-normal distribution with the given median. */
export function logNormal(random: () => number, median: number, spread: number): number {
  const u = Math.max(1e-9, random()), v = random();
  const normal = Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v);
  return median * Math.exp(spread * normal);
}

const hourWeights = [0.2, 0.1, 0.05, 0.03, 0.03, 0.06, 0.2, 0.6, 1.2, 2, 2.4, 2.2, 1.4, 1.8, 2.4, 2.4, 2.1, 1.6, 1.0, 0.8, 1.0, 1.1, 0.7, 0.4];
const pick = <T>(random: () => number, weights: readonly number[], items: readonly T[]): T => {
  const total = weights.reduce((sum, weight) => sum + weight, 0);
  let at = random() * total;
  for (let index = 0; index < items.length; index++) { at -= weights[index]; if (at <= 0) return items[index]; }
  return items[items.length - 1];
};

/**
 * Generates the fixture: `sessionCount` sessions between `first` and `today`. The number of sessions a day grows over the
 * period and falls on weekends; every session has a project, a model and a handful of cells.
 */
export function generateFixtureData(options: Readonly<{ today: string; first?: string; sessionCount?: number; seed?: number }>): FixtureData {
  const { today } = options;
  const first = options.first ?? "2026-04-20";
  const sessionCount = options.sessionCount ?? 906;
  const random = createRandom(options.seed ?? 20261009);
  const span = dayDistance(first, today) + 1;
  const days = Array.from({ length: span }, (_, index) => addDays(first, index));
  const dayWeights = days.map((day, index) => {
    const weekday = new Date(`${day}T00:00:00Z`).getUTCDay();
    return (weekday === 0 || weekday === 6 ? 0.35 : 1) * (0.5 + 1.1 * (index / span)) * (0.75 + random() * 0.5);
  });
  const projectWeights = [5, 2, 3, 1.4, 1, 1.2];
  const modelWeights = [4, 3, 1.5, 1.2, 0.6, 0.9];
  const sessions: FixtureSession[] = [];
  const cells: Cell[] = [];
  for (let number = 0; number < sessionCount; number++) {
    const id = `${(0x1a000000 + number * 7919).toString(16).padStart(8, "0")}-c20c-7ace-b180-${(0x47d01a11d62 + number).toString(16).padStart(12, "0")}`;
    const project = pick(random, projectWeights, fixtureProjects);
    const model = pick(random, modelWeights, fixtureModels);
    const day = pick(random, dayWeights, days);
    const parent = sessions.length > 20 && random() < 0.17 ? sessions.filter(session => session.project === project.id && session.first <= day).at(-1) ?? null : null;
    const origin: Cell["origin"] = parent ? "agent" : random() < 0.04 ? "automation" : random() < 0.04 ? "reminder" : "you";
    const lengthInDays = random() < 0.78 ? 1 : random() < 0.7 ? 2 : 3 + Math.floor(random() * 4);
    const title = `${verbs[Math.floor(random() * verbs.length)]} ${objects[Math.floor(random() * objects.length)]}`;
    let last = day;
    for (let offset = 0; offset < lengthInDays; offset++) {
      const cellDay = addDays(day, offset);
      if (cellDay > today) break;
      last = cellDay;
      const hours = 1 + Math.floor(random() * 3) + (random() < 0.15 ? 2 : 0);
      let hour = pick(random, hourWeights, hourWeights.map((_, index) => index));
      for (let step = 0; step < hours; step++) {
        cells.push(makeCell(random, { session: id, day: cellDay, hour: (hour + step) % 24, project: project.id, model, origin, delegated: parent !== null }));
      }
      hour = (hour + hours) % 24;
    }
    sessions.push({ id, title, project: project.id, provider: model.provider, model: model.model, parent: parent?.id ?? null, origin, first: day, last, deleted: random() < 0.05 });
  }
  cells.sort((a, b) => a.day < b.day ? -1 : a.day > b.day ? 1 : a.hour - b.hour);
  return { today, firstDay: first, projects: fixtureProjects, spaces: fixtureSpaces, models: fixtureModels, sessions, cells };
}

function makeCell(random: () => number, base: Readonly<{ session: string; day: string; hour: number; project: string; model: FixtureModel; origin: Cell["origin"]; delegated: boolean }>): Cell {
  const { model } = base;
  const effort = model.efforts[Math.floor(random() * model.efforts.length)];
  const runs = 1 + Math.floor(random() * (base.delegated ? 3 : 6));
  const runDurations = Array.from({ length: runs }, () => Math.round(logNormal(random, 95_000 / model.speed, 1.15)));
  const interrupted = random() < 0.03 ? 1 : 0;
  const failed = random() < 0.05 ? 1 : 0;
  const completed = Math.max(0, runs - interrupted - failed);
  const activeMs = Math.min(3_600_000, runDurations.reduce((sum, value) => sum + value, 0));
  const requests = Math.round(runs * (6 + random() * 18));
  const fresh = Math.round(requests * logNormal(random, 5200, 0.7));
  const cacheRead = Math.round(requests * logNormal(random, 52000, 0.6) * (model.provider === "mistral" ? 0.2 : 1));
  const cacheWrite = Math.round(requests * logNormal(random, 1800, 0.8) * (model.provider === "claude-code" ? 1 : 0.3));
  const output = Math.round(requests * logNormal(random, 620, 0.7));
  const reasoning = Math.round(output * (model.provider === "mistral" ? 0.05 : 0.2 + random() * 0.4));
  const promptsYou = base.origin === "you" ? runs - Math.floor(random() * Math.min(2, runs)) : 0;
  const promptsOther = base.origin === "you" ? (random() < 0.12 ? 1 : 0) : runs;
  const promptKinds: [number, number, number, number] = [Math.max(1, promptsYou + promptsOther - 1), random() < 0.3 ? 1 : 0, random() < 0.2 ? 1 : 0, random() < 0.15 ? 1 : 0];
  const words = Math.round(promptsYou * logNormal(random, 48, 0.9));
  const toolCount = Math.round(runs * (4 + random() * 22));
  const tools = fixtureTools.flatMap((definition, index) => {
    const share = [0.22, 0.1, 0.03, 0.16, 0.08, 0.2, 0.03, 0.06, 0.07, 0.05][index];
    const calls = Math.round(toolCount * share * (0.5 + random()));
    if (calls === 0) return [];
    const failures = Math.round(calls * definition.failure * (0.5 + random()));
    const timeMs = Math.round(calls * definition.median * (1.2 + random() * 0.8));
    const maxMs = Math.round(definition.median * (4 + random() * 30));
    const edits = definition.tool === "ToolCall:apply_patch" || definition.tool === "ToolCall:write_file";
    return [{ tool: definition.tool, kind: definition.kind, calls, failures, timeMs, maxMs, bytesIn: calls * Math.round(logNormal(random, 380, 0.7)), bytesOut: calls * Math.round(logNormal(random, 2600, 1)),
      linesAdded: edits ? Math.round(calls * logNormal(random, 24, 0.9)) : 0, linesRemoved: edits ? Math.round(calls * logNormal(random, 11, 0.9)) : 0, filesChanged: edits ? calls : 0 }];
  });
  const cost = model.costUnit === "usd" ? runs * (0.4 + random() * 3.2) : model.costUnit === "AI credits" ? Math.round(requests * (0.9 + random() * 2.4)) : 0;
  return {
    session: base.session, day: base.day, hour: base.hour, project: base.project, provider: model.provider, model: model.model, effort, origin: base.origin, delegated: base.delegated,
    runs, completed, failed, interrupted, activeMs, runDurations, promptsYou, promptsOther, promptKinds, promptChars: Math.round(words * 5.6), promptWords: words,
    attachments: [Math.round(random() * 1.4), random() < 0.1 ? 1 : 0, random() < 0.05 ? 1 : 0], answers: requests, requests, fresh, cacheRead, cacheWrite, output, reasoning,
    errors: failed + (random() < 0.03 ? 1 : 0), compactions: random() < 0.07 ? 1 : 0, contextFill: Math.min(0.98, (fresh + cacheRead) / Math.max(1, requests) / model.contextWindow * (1.5 + random())),
    costUnit: model.costUnit, cost, tools,
  };
}
