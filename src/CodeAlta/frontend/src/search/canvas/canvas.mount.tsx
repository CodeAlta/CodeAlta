// Disposable page: the production Search canvas over a fake `localSearch`, for canvas.browser.test.ts.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { LocalSearchHit, LocalSearchOverviewResponse, LocalSearchQueryResponse, LocalSearchStatusResponse } from "#neoastra";
import type { CanvasQuery, SearchCanvasApi } from "./api";
import { SearchCanvas, type SearchCanvasActions } from "./SearchCanvas";

document.documentElement.classList.add("bp6-dark");
document.documentElement.dataset.theme = "dark";

const day = (offset: number) => { const date = new Date(); date.setDate(date.getDate() - offset); return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`; };
const settings = { enabled: true, sessions: true, semantic: false, model: "", excludedProjects: ["p3"], excludedSpaces: [] };
const state = {
  status: { status: "ok", settings, semantic: null, busy: true, ready: false, journalsDone: 12, journalsTotal: 40, lastPass: null, lastError: "unreadable line in journal s9", meaning: "off",
    coveragePercent: 0, items: {}, chunks: 0, sessions: 0, kilobytes: 0, projects: {}, days: [] } as LocalSearchStatusResponse,
  overview: { status: "ok", items: { message: 18940, plan: 640, doc: 520, tool: 310, skill: 42, mcp: 3 }, chunks: 52_000, sessions: 915, recent: 312, kilobytes: 53_248,
    projects: { p1: 15_000, p2: 4_000 }, days: [{ day: day(0), turns: 40 }, { day: day(1), turns: 12 }, { day: day(30), turns: 3 }],
    sources: [{ family: "journal", sources: 915, lastCheck: new Date().toISOString(), failed: 1, lastError: "unreadable line" }, { family: "docs", sources: 1, lastCheck: new Date().toISOString(), failed: 0, lastError: null }],
    meaning: null } as LocalSearchOverviewResponse,
  reads: 0, queries: [] as CanvasQuery[], browses: [] as { kind: string; projectId: string | null }[], located: [] as string[],
  actions: [] as string[], watches: 0,
};
const hit = (over: Partial<LocalSearchHit>): LocalSearchHit => ({ kind: "message", ref: "msg:s1/c1", title: "Flaky tests", snippet: "the retry test waits for the port", marks: [{ start: 4, length: 5 }],
  at: new Date().toISOString(), match: "words", wordsRank: 3, meaningRank: null, rank: 1, sessionId: "s1", projectId: "p1", runId: "r1", role: "assistant", extra: null, ...over });
const hits = [hit({ meaningRank: 2, match: "both" }), hit({ kind: "doc", ref: "doc:worktrees.md#cleanup", title: "Worktrees › Cleanup", snippet: "A merged branch loses its worktree", marks: [], wordsRank: 1, rank: 2, sessionId: null,
  projectId: null, role: null, extra: '{"page":"worktrees.md","anchor":"cleanup"}', at: "1970-01-01T00:00:00+00:00" }), hit({ kind: "tool", ref: "tool:builtin/read_file", title: "read_file", rank: 3, wordsRank: null, meaningRank: 1, match: "meaning",
  sessionId: null, role: null, snippet: "Reads a file", marks: [] })];
const answer = (list: LocalSearchHit[]): LocalSearchQueryResponse => ({ status: "ok", hits: list, truncated: false, meaning: "off", milliseconds: 4 });

// The watch: each push is an event of the host; the test pushes them.
let push: (() => void) | null = null;
const api: SearchCanvasApi = {
  status: async () => { state.reads++; return state.status; },
  overview: async () => state.overview,
  query: async request => { state.queries.push(request); return answer(request.kind === "doc" ? hits.filter(item => item.kind === "doc") : hits); },
  browse: async (kind, projectId) => { state.browses.push({ kind, projectId }); return answer([hit({ kind: "plan", ref: "plan:p1/pelican", title: "Pelican migration", snippet: "Move the pelican service",
    marks: [], wordsRank: null, sessionId: null, role: null, extra: '{"id":"pelican"}' })]); },
  locate: async ref => { state.located.push(ref); return { status: "ok", sessionId: "s1", projectId: "p1", offset: "4242" }; },
  watch: async signal => {
    state.watches++;
    return { [Symbol.asyncIterator]: () => {
      let first = true;
      return { next: () => first ? (first = false, Promise.resolve({ value: { revision: 0 }, done: false }))
        : new Promise(resolve => { push = () => resolve({ value: { revision: 1 }, done: false }); signal.addEventListener("abort", () => resolve({ value: undefined, done: true })); }) };
    } };
  },
};
const actions: SearchCanvasActions = {
  openSession: id => { state.actions.push(`session:${id}`); },
  openMessage: (id, offset) => { state.actions.push(`message:${id}@${offset}`); },
  openFile: path => { state.actions.push(`file:${path}`); },
  openDocumentation: (page, anchor) => { state.actions.push(`doc:${page}#${anchor}`); },
  runCommand: name => { state.actions.push(`command:${name}`); return true; },
  notify: message => { state.actions.push(`notify:${message}`); },
};
const projects = [{ id: "p1", name: "CodeAlta", path: "/code/codealta" }, { id: "p2", name: "NeoAstra", path: "/code/neoastra" }, { id: "p3", name: "Tomlyn", path: "/code/tomlyn" }];
const colors = { series: ["#2d72d2", "#29a634", "#d9822b", "#7961db"], muted: "#8f99a8" };

let show: (visible: boolean) => void = () => { };
function Page() {
  const [visible, setVisible] = useState(true);
  show = setVisible;
  return <SearchCanvas api={api} projects={projects} visible={visible} colors={colors} actions={actions} />;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Page /></StrictMode>);

/** Search by meaning on, indexing: a quarter of the space has vectors, today and two days ago are done, yesterday is by words only. */
function meaningOn() {
  state.status = { ...state.status, settings: { ...settings, semantic: true }, semantic: { supported: true, enabled: true, state: "indexing", modelId: "granite-97m-multilingual-r2",
    embedded: 13_000, total: 52_000, secondsLeft: 600, chunksPerSecondTenths: 412, error: null,
    download: { state: "idle", modelId: null, receivedKilobytes: 0, totalKilobytes: 0, error: null }, runtimeInstalled: true, models: [], offer: false, sessions: 915 } };
  state.overview = { ...state.overview, meaning: { chunks: 52_000, embedded: 13_000,
    days: [{ day: day(0), chunks: 80, embedded: 80 }, { day: day(1), chunks: 30, embedded: 10 }, { day: day(30), chunks: 6, embedded: 6 }] } };
}

(window as unknown as { searchFixture: unknown }).searchFixture = {
  state,
  /** Turns search by meaning on, as the host would say it, and tells the canvas. */
  meaningOn: () => { meaningOn(); push?.(); push = null; },
  /** Sends an event of the host: the index changed. */
  change: () => { push?.(); push = null; },
  setVisible: (visible: boolean) => show(visible),
  setTheme(dark: boolean) { document.documentElement.classList.toggle("bp6-dark", dark); document.documentElement.dataset.theme = dark ? "dark" : "light"; },
};
