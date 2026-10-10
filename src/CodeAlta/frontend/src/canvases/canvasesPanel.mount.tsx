// Disposable page; the Canvases tab and the search over a hub whose catalog the test plays.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { CanvasItem, WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { ProjectRailRows } from "../explorer/ProjectRailRows";
import { canvasTab, type FileTab } from "../fileTabs";
import { GlobalSearch } from "../search/GlobalSearch";
import { CanvasesPanel } from "./CanvasesPanel";
import { canvasMenuItems, defaultCanvasTarget, type CanvasSelection, type CanvasTarget } from "./canvasPages";

const root = createRoot(document.getElementById("root")!);
const item = (id: string, scope: string, fields: Partial<CanvasItem> = {}): CanvasItem => ({ pluginKey: "global:tools", plugin: "Tools", package: "plugin:global:tools", id,
  title: id[0].toUpperCase() + id.slice(1), description: null, icon: null, iconData: null, scope, input: false, actions: 0, describes: false, ...fields });

// What the host declares now, and what a listing of it is: the page asks again when it comes to the screen.
let catalog: readonly CanvasItem[] = [];
const listeners = new Set<() => void>();
const state = { refreshed: 0, opened: [] as { id: string; project: string | null; session: string | null }[], created: 0, activated: 0, searched: [] as string[], chosen: [] as string[] };
const hub = {
  getCatalog: () => catalog,
  subscribeCatalog: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
  refresh: async () => { state.refreshed++; },
};

const projects = [{ id: "p1", path: "/code/one", name: "One" }, { id: "p2", path: "/code/two", name: "Two" }];
const sessions = [{ id: "s1", title: "First session", project: { id: "p1", path: "/code/one" }, projectName: "One" }, { id: "s2", title: "A chat", project: null, projectName: null }];
const selected: CanvasSelection = { project: { id: "p1", path: "/code/one" }, session: { id: "s1", project: { id: "p1", path: "/code/one" } } };
const nothing: CanvasSelection = { project: null, session: null };

const fixture = {
  state, projects, sessions,
  item,
  /** Replaces what the plugins declare, as a new listing does. */
  declare(items: readonly CanvasItem[]) { catalog = items; for (const listener of [...listeners]) listener(); },
  /** The page, under StrictMode as in the application. */
  page(options: { visible?: boolean; selection?: "selected" | "nothing"; tabs?: FileTab[]; canNew?: boolean } = {}) {
    flushSync(() => root.render(createElement(StrictMode, null, createElement(CanvasesPanel, {
      hub, tabs: options.tabs ?? [], projects, sessions, selection: options.selection === "nothing" ? nothing : selected, visible: options.visible ?? true,
      onActivate: () => { state.activated++; },
      onOpen: (value: CanvasItem, target: CanvasTarget) => { state.opened.push({ id: value.id, project: target.project?.id ?? null, session: target.sessionId }); },
      onNew: options.canNew === false ? null : () => { state.created++; },
    }))));
  },
  /** The tab of an open canvas, as the window keeps it. */
  tab: (value: CanvasItem, project: string | null = null, session: string | null = null) => canvasTab({ pluginKey: value.pluginKey, canvasId: value.id,
    project: project ? projects.find(candidate => candidate.id === project) : null, sessionId: session }, { title: value.title }),
  /** The search over the canvases the plugins declare. */
  search(options: { text?: string; selection?: "selected" | "nothing" } = {}) {
    const selection = options.selection === "nothing" ? nothing : selected;
    flushSync(() => root.render(createElement(StrictMode, null, createElement(GlobalSearch, {
      snapshot: null, favorites: [], start: { text: options.text ?? "" }, files: null, available: () => true, onCommand: () => { }, canvases: catalog,
      canvasAvailable: (value: CanvasItem) => !!defaultCanvasTarget(value, selection),
      onCanvas: (value: CanvasItem) => { state.chosen.push(value.id); },
      onProject: () => { }, onSession: () => { }, onFile: () => { }, onClose: () => { state.searched.push("closed"); },
    }))));
  },
  /** The row of a project, whose menu lists the canvases of its scope the way the Explorer does. */
  projectMenu() {
    const project: WorkspaceProject = { id: "p1", path: "/code/one", name: "One", archived: false };
    const snapshot: WorkspaceSnapshot = { configured: true, projects: [project], sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
    const nothing = () => { };
    flushSync(() => root.render(createElement("div", { className: "ide-shell", style: { width: 272 } }, createElement("aside", { className: "project-rail" }, createElement(ProjectRailRows, {
      projects: [project], selectedId: "p1", onSelect: nothing, canRename: true, renameBusy: false, onRename: nothing,
      actions: { current: () => ({ snapshot, projectId: "p1", sessionId: null, hostEpoch: "epoch", hostAvailable: true, refreshVersion: 1, refreshReady: true, active: true,
        generation: 1, modalGeneration: 1, canMutate: true, locked: false }), open: nothing, rename: nothing, archive: nothing,
        canvases: { list: () => canvasMenuItems(catalog, "Project"), open: (value: CanvasItem, row: WorkspaceProject) => { state.opened.push({ id: value.id, project: row.id, session: null }); },
          all: () => { state.chosen.push("page"); } } },
    })))));
  },
  /** What a menu of a row lists. */
  menu: (scope: "Project" | "Session") => canvasMenuItems(catalog, scope),
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { canvasesFixture: fixture });
