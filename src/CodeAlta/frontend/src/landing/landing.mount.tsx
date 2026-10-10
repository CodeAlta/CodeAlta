// Disposable page; the production landing page over a shell of the window that the test plays.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { PluginUiContext, noPluginContributions, type PluginPane, type PluginUiValue } from "../pluginUi";
import { LandingCanvas } from "./LandingCanvas";
import { createLandingPreferences, type LandingPreferenceStore } from "./landingPreferences";
import { landingCanvasId, landingPluginKey, type LandingProject, type LandingProviders, type LandingSession, type LandingShell } from "./landingShell";
import { useLandingAtStartup } from "./landingWindow";

document.documentElement.classList.add("bp6-dark");
const root = createRoot(document.getElementById("root")!);
type Wire = Record<string, unknown>;

/** What the page did through the shell, and what the shell answers. */
const fresh = () => ({
  runs: [] as string[],
  // The commands the window cannot run now: `run` answers false for them.
  refused: [] as string[],
  sessions: [] as string[], projects: [] as string[],
  commands: [] as { commandId: string; projectId: string | null; label: string }[],
  canvases: [] as { pluginKey: string; canvasId: string; projectId: string | null; key: string | null }[],
  // What the page said could not be done now.
  unavailable: [] as string[],
  named: [] as { name: string; pluginKey: string | null; pane: Partial<PluginPane> | undefined }[],
  cardReads: 0, providerReads: 0,
  // What the host answers when the cards are read, or a failure.
  cards: [] as Wire[], cardsFail: false,
  providers: { ready: 1, detecting: false } as LandingProviders | null,
  started: [] as string[],
});
const state = fresh();

type Shown = { visible: boolean; dark: boolean; epoch: string | null; projects: readonly LandingProject[] | null; sessions: readonly LandingSession[] | null; width: number | null; space: LandingShell["space"] };
const first = (): Shown => ({ visible: true, dark: true, epoch: "epoch-1", projects: [], sessions: [], width: null, space: { id: "all", name: "All projects", isDefault: true } });
let shown = first();

const storage = new Map<string, string>();
const memory = { getItem: (key: string) => storage.get(key) ?? null, setItem: (key: string, value: string) => { storage.set(key, value); } };
let preferences: LandingPreferenceStore = createLandingPreferences(memory);

// The functions of the shell keep their identity from one drawing to the next, as the ones the window lends do.
const lent: Omit<LandingShell, "epoch" | "space" | "projects" | "sessions"> = {
  readProviders: async () => { state.providerReads++; return state.providers; },
  readCards: async () => {
    state.cardReads++;
    if (state.cardsFail) throw new Error("host gone");
    return { status: "ok", cards: state.cards };
  },
  run: name => { state.runs.push(name); return !state.refused.includes(name); },
  notifyUnavailable: label => { state.unavailable.push(label); },
  openProject: projectId => { state.projects.push(projectId); },
  openSession: sessionId => { state.sessions.push(sessionId); },
  runCardCommand: (commandId, projectId, label) => { state.commands.push({ commandId, projectId, label }); },
};
const ui: PluginUiValue = { epoch: "epoch-1", projectId: null, contributions: noPluginContributions, run: () => { },
  runNamed: (name, pluginKey, pane) => { state.named.push({ name, pluginKey, pane }); } };
const openCanvas = (request: { pluginKey: string; canvasId: string; projectId: string | null; key: string | null }) => { state.canvases.push({ ...request }); };

/** A card as the host lists it; the test overrides what it wants. */
function card(over: Wire): Wire {
  const id = String(over.cardId ?? "card");
  return { id: `builtin:fixture/${id}`, pluginKey: "builtin:fixture", plugin: "Fixture", cardId: id, title: id, icon: "star", iconData: null, state: "ok", html: `<p>${id}</p>`,
    statusText: null, tone: "Info", projectId: null, projectName: null, actions: [], commands: [], ...over };
}
const action = (over: Wire): Wire => ({ label: "Action", icon: null, iconData: null, commandId: null, canvas: null, canvasScope: null, key: null, primary: false, disabled: false, ...over });
const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

/** Three projects and four sessions, one of them started by another session. */
function sample(): Pick<Shown, "projects" | "sessions"> {
  return {
    projects: [{ id: "p1", name: "Alpha", path: "/code/alpha" }, { id: "p2", name: "Beta", path: "/code/beta" }, { id: "p3", name: "Gamma", path: "/code/gamma" }],
    sessions: [
      { id: "s-old", title: "Old one", projectId: "p1", updatedAt: ago(3 * 24 * 60), child: false },
      { id: "s-child", title: "Sub-agent work", projectId: "p1", updatedAt: ago(1), child: true },
      { id: "s-new", title: "## Newest", projectId: "p2", updatedAt: ago(60), child: false },
      { id: "s-chat", title: "A chat", projectId: null, updatedAt: ago(24 * 60), child: false },
    ],
  };
}

/** The decision of the start, as the shell of the window takes it. */
function Startup({ ready, catalog }: { ready: boolean; catalog: readonly Readonly<{ pluginKey: string; id: string }>[] }) {
  useLandingAtStartup(ready, catalog, item => { state.started.push(`${item.pluginKey}/${item.id}`); }, preferences);
  return null;
}

function draw() {
  const shell: LandingShell = { ...lent, epoch: shown.epoch, space: shown.space, projects: shown.projects, sessions: shown.sessions };
  // Under StrictMode, as in the application: React then runs each effect of a new component twice.
  flushSync(() => root.render(createElement(StrictMode, null, createElement(PluginUiContext.Provider, { value: ui },
    createElement("div", { id: "frame", style: { width: shown.width ?? undefined, padding: 12, boxSizing: "border-box" } },
      createElement(LandingCanvas, { shell, visible: shown.visible, dark: shown.dark, openCanvas, preferences }))))));
}

const fixture = {
  state, card, action, sample, ago,
  /** Draws the page with what changed since it was last drawn. */
  show(change: Partial<Shown> = {}) { shown = { ...shown, ...change }; draw(); },
  /** Takes the page away and forgets what it did, what the shell answers and what the user chose. */
  reset(kept: Record<string, string> = {}) {
    flushSync(() => root.render(null));
    Object.assign(state, fresh());
    shown = first();
    storage.clear();
    for (const [key, value] of Object.entries(kept)) storage.set(key, value);
    preferences = createLandingPreferences(memory);
  },
  /** A plugin said that its cards changed, or plugins started or stopped. */
  invalidate(name = "codealta:landing-cards-changed") { window.dispatchEvent(new Event(name)); },
  preferences: () => preferences.get(),
  setPreference(name: "openAtStartup" | "animate", value: boolean) { flushSync(() => preferences.set(name, value)); },
  stored: () => Object.fromEntries(storage),
  /** The start of the window: its tabs are there or not, and the canvas of the page is declared or not. */
  startup(options: { ready: boolean; declared: boolean }) {
    const catalog = [{ pluginKey: "builtin:statistics", id: "statistics" }, { pluginKey: landingPluginKey, id: "other" }, ...(options.declared ? [{ pluginKey: landingPluginKey, id: landingCanvasId }] : [])];
    flushSync(() => root.render(createElement(StrictMode, null, createElement(Startup, { ready: options.ready, catalog }))));
  },
};
Object.assign(window, { landingFixture: fixture });
