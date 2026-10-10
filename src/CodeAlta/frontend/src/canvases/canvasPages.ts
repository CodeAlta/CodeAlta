// How a canvas is found and opened: the pieces of the Canvases page, of the search, of the menus and of the requests of
// plugins and agents that need no component. The tab itself is `fileTabs.ts`; what a tab shows is `CanvasPanel`.
import type { CanvasItem, WorkspaceSession } from "#neoastra";
import type { SessionMenuEntry } from "../SessionTabMenu";
import { canvasTab, closeFileTab, emptyFileTabs, fileTabKey, isCanvasTab, openFileTab, persistFileTabs, restoreFileTabs, sameFileTab, type FileTab, type FileTabs } from "../fileTabs";
import { spaceShows, type Space } from "../spaces/spaces";

/** What one instance of a canvas is about. */
export type CanvasScope = "Application" | "Project" | "Session";

/** The scope a canvas declares; a name the host does not say is the application. */
export const canvasScope = (item: Pick<CanvasItem, "scope">): CanvasScope => item.scope === "Project" ? "Project" : item.scope === "Session" ? "Session" : "Application";

/** What `alta canvas` calls a canvas: the plugin and the canvas. */
export const canvasRef = (item: Pick<CanvasItem, "pluginKey" | "id">) => `${item.pluginKey}/${item.id}`;

/** The project a canvas is about, as a tab names it. */
export type CanvasProject = Readonly<{ id: string; path: string }>;

/** What a canvas is opened for: the project of a project canvas, and the session of a session canvas with the project of that session. */
export type CanvasTarget = Readonly<{ project: CanvasProject | null; sessionId: string | null }>;

/** What the window has selected, which a canvas that is opened without a choice is about. */
export type CanvasSelection = Readonly<{
  /** The project in front, when it can be worked in; null for a chat or an archived project. */
  project: CanvasProject | null;
  /** The session in front, with its project; null when none is. */
  session: Readonly<{ id: string; project: CanvasProject | null }> | null;
}>;

/**
 * What a canvas is opened for when nothing was chosen: the application has nothing to name, a project canvas takes the project in
 * front, a session canvas the session in front. Null when the scope needs what the window does not have selected.
 */
export function defaultCanvasTarget(item: Pick<CanvasItem, "scope">, selection: CanvasSelection): CanvasTarget | null {
  switch (canvasScope(item)) {
    case "Application": return { project: null, sessionId: null };
    case "Project": return selection.project ? { project: selection.project, sessionId: null } : null;
    default: return selection.session ? { project: selection.session.project, sessionId: selection.session.id } : null;
  }
}

/** The tab that shows an instance of a canvas. The title, the icon and the plugin are what the declaration says until the plugin says better. */
export function canvasTabOf(item: CanvasItem, target: CanvasTarget): FileTab {
  const scope = canvasScope(item);
  return canvasTab({ pluginKey: item.pluginKey, canvasId: item.id, project: scope === "Application" ? null : target.project,
    sessionId: scope === "Session" ? target.sessionId : null }, { title: item.title, icon: item.icon, plugin: item.package });
}

/** Whether a tab shows an instance of a canvas. */
export const showsCanvas = (tab: FileTab, item: Pick<CanvasItem, "pluginKey" | "id">) => tab.view === "canvas" && tab.pluginKey === item.pluginKey && tab.canvasId === item.id;

/** The tabs of a space that show a canvas. */
export const openCanvases = (tabs: readonly FileTab[], item: Pick<CanvasItem, "pluginKey" | "id">) => tabs.filter(tab => showsCanvas(tab, item));

/** The most canvases a menu lists before "More…". */
export const canvasMenuLimit = 4;

/** The canvases of a scope that a row menu lists: the first few, and whether more are left for the page. */
export function canvasMenuItems(items: readonly CanvasItem[], scope: CanvasScope, limit = canvasMenuLimit): Readonly<{ items: readonly CanvasItem[]; more: boolean }> {
  const found = items.filter(item => canvasScope(item) === scope);
  return { items: found.slice(0, limit), more: found.length > limit };
}

/** The project of a scope of the Explorer, as the snapshot of the workspace has it. */
export type CanvasScopeProject = Readonly<{ id: string; path: string; archived?: boolean }>;

/**
 * What a session canvas opened from the menu of a session row is about: the session of the row, with the project of the scope the
 * row is listed under. That project is the one of the row, never the one that happens to be selected: a session of another project
 * takes its own project, and a chat takes none. A project that is archived, or that is not the one of the session, is not named.
 */
export function sessionCanvasTarget(session: Pick<WorkspaceSession, "id" | "scopeKind" | "projectId">, scopeProject: CanvasScopeProject | null | undefined): CanvasTarget {
  const named = scopeProject && !scopeProject.archived && session.scopeKind === "project" && session.projectId === scopeProject.id;
  return { project: named ? { id: scopeProject.id, path: scopeProject.path } : null, sessionId: session.id };
}

/**
 * What the menu of a session row offers of the canvases of plugins: the canvases about a session, a few lines and then the page of
 * the canvases, after a separator; nothing when no plugin declares one. Each line opens its canvas for the session of the row
 * (`sessionCanvasTarget`).
 */
export function sessionCanvasMenuEntries(items: readonly CanvasItem[], session: Pick<WorkspaceSession, "id" | "scopeKind" | "projectId">, scopeProject: CanvasScopeProject | null | undefined,
  actions: Readonly<{ label: (title: string) => string; more: string; open: (item: CanvasItem, target: CanvasTarget) => void; openPage: () => void }>): SessionMenuEntry[] {
  const menu = canvasMenuItems(items, "Session");
  if (menu.items.length === 0) return [];
  const target = sessionCanvasTarget(session, scopeProject);
  return [{ key: "canvases", divider: true },
    ...menu.items.map(item => ({ key: `canvas:${item.pluginKey}/${item.id}`, label: actions.label(item.title), icon: "canvases" as const, onSelect: () => actions.open(item, target) })),
    ...(menu.more ? [{ key: "canvases-more", label: actions.more, icon: "canvases" as const, onSelect: actions.openPage }] : [])];
}

/** What the palette and the search list for a canvas: the same row, by its words. */
export type CanvasCommand = Readonly<{ key: string; item: CanvasItem; name: string; label: string; description: string; group: string }>;

/** A slash name that the author of a canvas never wrote: `open_<id>`. */
export const canvasCommandName = (item: Pick<CanvasItem, "id">) => `open_${item.id.replace(/[^A-Za-z0-9_]+/gu, "_")}`;

/** The commands of the canvases the plugins declare now: one for each, in the order of the declarations. */
export function canvasCommands(items: readonly CanvasItem[], label: (title: string) => string, fallback: (plugin: string) => string): readonly CanvasCommand[] {
  return items.map(item => ({ key: `canvas:${canvasRef(item)}`, item, name: canvasCommandName(item), label: label(item.title),
    description: item.description ?? fallback(item.plugin), group: item.plugin }));
}

/** Whether every word of a query is found in a command: its slash name, its title, its description or its plugin. */
export function matchesCanvasCommand(command: CanvasCommand, words: readonly string[]): boolean {
  const text = `${command.name} ${command.label} ${command.item.title} ${command.description} ${command.group} canvas`.toLowerCase();
  return words.every(word => text.includes(word));
}

/** What the "New canvas" action asks of the agent of the session it starts. It is a prompt to an agent: it stays in English. */
export const newCanvasPrompt = "Activate the codealta-plugin-runtime skill, then create a CodeAlta canvas for this project: a tab that a plugin provides. The canvas should show ";

/**
 * Where a canvas tab goes when a plugin or an agent asks for it: the space it names when the window has that space and the space shows the
 * project of the canvas; the space that is shown otherwise, when it shows the project; none when neither does.
 */
export function canvasRequestSpace(spaces: readonly Space[], requested: string | null, shown: string, projectId: string | null): string | null {
  const named = requested && spaces.some(space => space.id === requested) ? requested : shown;
  const space = spaceShows(spaces, named, projectId) ? named : shown;
  return spaceShows(spaces, space, projectId) ? space : null;
}

/** Opens a canvas tab in tabs, or finds it there; it comes to the front only when asked. */
export function bringCanvasTab(state: FileTabs, tab: FileTab, focus: boolean, keep: (tab: FileTab) => boolean = () => false): FileTabs {
  const next = openFileTab(state, tab, keep);
  return focus ? next : { ...next, active: state.active };
}

/**
 * Adds a canvas tab to a space that the window does not show, and does not move the window: the tabs the window kept of that space in this
 * run, else the ones it stored, get the tab, and what comes out is stored again for the next time the space is shown. A tab is in front
 * of its space only when asked, as it is in the space that is shown. Removed canvas tabs notify their owner: their panels already
 * left the page with the space, and cannot release an instance again when capacity evicts its tab.
 */
export function addCanvasTabToSpace(source: Readonly<{ kept: FileTabs | undefined; read: () => string | null; write: (value: string) => void; closed?: (tab: FileTab) => void }>,
  tab: FileTab, focus: boolean, keep: (tab: FileTab) => boolean = () => false): FileTabs {
  const previous = source.kept ?? restoreFileTabs(source.read) ?? emptyFileTabs();
  const next = bringCanvasTab(previous, tab, focus, keep);
  persistFileTabs(source.write, next);
  for (const removed of previous.open) {
    if (isCanvasTab(removed) && !next.open.some(value => sameFileTab(value, removed))) source.closed?.(removed);
  }
  return next;
}

/**
 * What names the status of a canvas tab: the tab and the space it is in. The same canvas can be a tab of several spaces, each with an
 * instance of its own, and so a status of its own.
 */
export const canvasStatusKey = (tab: FileTab, space: string) => `${space}\n${fileTabKey(tab)}`;

/**
 * The statuses of the canvas tabs with the one a tab of a space has now; null or an empty text for none. The same map when nothing changes.
 */
export function withCanvasStatus(statuses: ReadonlyMap<string, string>, tab: FileTab, space: string, status: string | null): ReadonlyMap<string, string> {
  const key = canvasStatusKey(tab, space), value = status || undefined;
  if (statuses.get(key) === value) return statuses;
  const next = new Map(statuses);
  if (value) next.set(key, value); else next.delete(key);
  return next;
}

/**
 * What becomes of an instance that the host opened for a tab that went away meanwhile, given the tabs its space has now: it is closed when
 * the tab is no longer one of them (the tab was closed, and nothing else would close the instance), and only hidden when it still is (the
 * tab left the page with its space).
 */
export const abandonedCanvas = (open: readonly FileTab[], tab: FileTab): "close" | "hide" => open.some(value => sameFileTab(value, tab)) ? "hide" : "close";

/**
 * The tab of an instance that a plugin closed, as the host names the instance. It is the tab the window has for it, whatever the title
 * and the folder of the project that tab carries: a tab is told from another by the ids alone.
 */
export const closedCanvasTab = (closed: Readonly<{ pluginKey: string; canvasId: string; projectId: string | null; sessionId: string | null; key: string | null }>): FileTab =>
  canvasTab({ pluginKey: closed.pluginKey, canvasId: closed.canvasId, project: closed.projectId ? { id: closed.projectId, path: "" } : null, sessionId: closed.sessionId, key: closed.key });

/**
 * Takes a canvas tab out of a space that the window does not show, because its plugin closed the instance: the tabs the window kept of
 * that space in this run, else the ones it stored, lose the tab, and what comes out is stored again, so the tab is not there when the
 * space is shown. Null when the space has no tabs kept or stored: nothing is written for it.
 */
export function removeCanvasTabFromSpace(source: Readonly<{ kept: FileTabs | undefined; read: () => string | null; write: (value: string) => void }>, tab: FileTab): FileTabs | null {
  const state = source.kept ?? restoreFileTabs(source.read);
  if (!state) return null;
  // The tab the space has, with its title and its folder: it is the one that can be opened again.
  const open = state.open.find(value => sameFileTab(value, tab));
  if (!open) return state;
  const next = closeFileTab(state, open);
  persistFileTabs(source.write, next);
  return next;
}
