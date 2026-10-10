import type { WorkspaceSnapshot } from "#neoastra";

/**
 * One tab of a project beside the sessions: its code editor (`view: "editor"`, with the files opened in it as
 * tabs of its own), the changed files of its repository (`view: "changes"`), or one of its terminals
 * (`view: "terminal"`). A project has one editor and one changes tab at most, and a tab for each terminal shown.
 * The tab of a terminal names the terminal; its project is empty for a terminal of no project, and its path
 * is the folder the terminal started in. The automations of the application have one tab, of no project
 * (`view: "automations"`), and so have the work items, the tasks and the plans of every project
 * (`view: "workItems"`), the issues and pull requests (`view: "issues"`), and the canvases that plugins declare (`view: "canvases"`). The code editor also opens on the folder of a source plugin or of a skill, and on the folder of a
 * file that a link named and no project has: its tab names that folder where a tab names a project, and carries the name of the plugin, of the skill or of the folder.
 *
 * A canvas is a tab that a plugin provides (`view: "canvas"`). Its identity is the plugin, the canvas, the project or the
 * session it is about, and a key; its project is empty for a canvas of the application. The tab also carries what the strip
 * shows before the plugin has started: the title (`name`) and the `icon`, kept up to date by the plugin, and `plugin`, the id
 * of the folder of the plugin package, which says where to rebuild it and open its source when it is not running.
 */
export type FileTab = Readonly<{ projectId: string; projectPath: string; view: "editor" | "changes" | "terminal" | "automations" | "workItems" | "issues" | "canvases" | "canvas";
  terminalId?: string; name?: string; pluginKey?: string; canvasId?: string; sessionId?: string; key?: string; icon?: string; plugin?: string }>;
export type FileTabs = Readonly<{ open: readonly FileTab[]; active: FileTab | null; closed: readonly FileTab[] }>;
export const fileTabsKey = "codealta.desktop.fileTabs.v1";
export const fileTabLimit = 32;
export const emptyFileTabs = (): FileTabs => ({ open: [], active: null, closed: [] });
export const fileTabKey = (tab: FileTab) => JSON.stringify(tab.view === "terminal" ? [tab.projectId, tab.view, tab.terminalId]
  : tab.view === "canvas" ? [tab.sessionId ? "" : tab.projectId, tab.view, tab.pluginKey, tab.canvasId, tab.sessionId ?? "", tab.key ?? ""] : [tab.projectId, tab.view]);
export const isChangesTab = (tab: FileTab) => tab.view === "changes";
export const isEditorTab = (tab: FileTab) => tab.view === "editor";
export const isTerminalTab = (tab: FileTab) => tab.view === "terminal";
export const isAutomationsTab = (tab: FileTab) => tab.view === "automations";
/** The tab of the automations: there is one, whatever the project. */
export const automationsTab: FileTab = Object.freeze({ projectId: "", projectPath: "", view: "automations" });
/** The tab of the work items: there is one, whatever the project. */
export const isWorkItemsTab = (tab: FileTab) => tab.view === "workItems";
export const workItemsTab: FileTab = Object.freeze({ projectId: "", projectPath: "", view: "workItems" });
/** The tab of the issues and the pull requests: there is one, whatever the project. */
export const isIssuesTab = (tab: FileTab) => tab.view === "issues";
export const issuesTab: FileTab = Object.freeze({ projectId: "", projectPath: "", view: "issues" });
/** The tab of the canvases that plugins declare, the page that lists and opens them: there is one, whatever the project. */
export const isCanvasesTab = (tab: FileTab) => tab.view === "canvases";
export const canvasesTab: FileTab = Object.freeze({ projectId: "", projectPath: "", view: "canvases" });
export const isCanvasTab = (tab: FileTab) => tab.view === "canvas";
/** Which canvas a tab shows, and about what: the plugin and the canvas it declares, the project and the session when it has them, and its key. */
export type CanvasTabIdentity = Readonly<{ pluginKey: string; canvasId: string; project?: Readonly<{ id: string; path: string }> | null; sessionId?: string | null; key?: string | null }>;
/** What a tab of a canvas shows besides its identity: the title and the icon the plugin gave, and the folder of the plugin. */
export type CanvasTabLook = Readonly<{ title?: string | null; icon?: string | null; plugin?: string | null }>;
export const canvasIdPattern = /^[A-Za-z0-9._-]{1,64}$/;
/** The tab of an instance of a canvas. Two tabs with the same identity are one tab, whatever their title. */
export const canvasTab = (identity: CanvasTabIdentity, look: CanvasTabLook = {}): FileTab => ({
  projectId: identity.project?.id ?? "", projectPath: identity.project?.path ?? "", view: "canvas", pluginKey: identity.pluginKey, canvasId: identity.canvasId,
  ...identity.sessionId ? { sessionId: identity.sessionId } : {}, ...identity.key ? { key: identity.key } : {},
  ...look.title ? { name: look.title } : {}, ...look.icon ? { icon: look.icon } : {}, ...look.plugin ? { plugin: look.plugin } : {},
});
/**
 * Gives a canvas tab the title, the icon and the plugin its plugin gave, where it is: its place, its identity and which tab is
 * active do not change. What the look does not say stays. The same state when nothing changes.
 */
export function refreshCanvasTab(state: FileTabs, tab: FileTab, look: CanvasTabLook): FileTabs {
  const refreshed = (value: FileTab): FileTab => {
    if (!sameFileTab(value, tab)) return value;
    const title = look.title === undefined ? value.name : look.title || undefined, icon = look.icon === undefined ? value.icon : look.icon || undefined;
    const plugin = look.plugin === undefined ? value.plugin : look.plugin || undefined;
    if (title === value.name && icon === value.icon && plugin === value.plugin) return value;
    const { name: _name, icon: _icon, plugin: _plugin, ...identity } = value;
    return { ...identity, ...title ? { name: title } : {}, ...icon ? { icon } : {}, ...plugin ? { plugin } : {} };
  };
  const open = state.open.map(refreshed), closed = state.closed.map(refreshed);
  if (open.every((value, index) => value === state.open[index]) && closed.every((value, index) => value === state.closed[index])) return state;
  return { open, active: state.active && open.find(value => sameFileTab(value, state.active)) || state.active, closed };
}
/** The tab of a terminal. */
export const terminalTab = (terminal: Readonly<{ id: string; projectId: string | null; folder: string }>): FileTab =>
  ({ projectId: terminal.projectId ?? "", projectPath: terminal.folder, view: "terminal", terminalId: terminal.id });
/** The tab of a project's changes. */
export const changesTab = (project: Readonly<{ id: string; path: string }>): FileTab => ({ projectId: project.id, projectPath: project.path, view: "changes" });
/** The tab of a project's code editor. */
export const editorTab = (project: Readonly<{ id: string; path: string }>): FileTab => ({ projectId: project.id, projectPath: project.path, view: "editor" });
/** What the id of the folder of a source plugin starts with. The host gives the id, and finds the folder from it. */
export const pluginFolderPrefix = "plugin:";
/** The tab of the code editor on the folder of a source plugin. */
export const pluginEditorTab = (folder: Readonly<{ id: string; path: string; name: string }>): FileTab =>
  ({ projectId: folder.id, projectPath: folder.path, view: "editor", name: folder.name });
export const isPluginTab = (tab: FileTab) => tab.view === "editor" && tab.projectId.startsWith(pluginFolderPrefix);
/** What the id of the folder of a skill starts with. The host gives the id, and finds the folder from it. */
export const skillFolderPrefix = "skill:";
/** The tab of the code editor on the folder of a skill. */
export const skillEditorTab = (folder: Readonly<{ id: string; path: string; name: string }>): FileTab =>
  ({ projectId: folder.id, projectPath: folder.path, view: "editor", name: folder.name });
export const isSkillTab = (tab: FileTab) => tab.view === "editor" && tab.projectId.startsWith(skillFolderPrefix);
/**
 * What the id of a folder of the disk starts with: the folder of a file that a link names and that no project
 * has. The host gives the id when the link is followed, and knows the folder while it runs.
 */
export const diskFolderPrefix = "folder:";
/** The tab of the code editor on a folder of the disk that is no project. */
export const diskEditorTab = (folder: Readonly<{ id: string; path: string; name: string }>): FileTab =>
  ({ projectId: folder.id, projectPath: folder.path, view: "editor", name: folder.name });
export const isDiskFolderTab = (tab: FileTab) => tab.view === "editor" && tab.projectId.startsWith(diskFolderPrefix);
/** A tab of the code editor on a folder that is no project of the workspace: the one of a plugin, of a skill, or of a file a link named. */
export const isFolderTab = (tab: FileTab) => isPluginTab(tab) || isSkillTab(tab) || isDiskFolderTab(tab);
/** Whether the folder of a skill is only read: the skill is not one of the user or of a project. */
export const skillReadOnly = (source: string) => !["ProjectAlta", "ProjectCommon", "UserAlta", "UserCommon", "ProjectCopilot", "UserCopilot"].includes(source);
/**
 * What the id of a folder of the disk starts with when the host gave it for one file (a configuration file whose
 * folder holds more than settings), and when it gave it to be only read (what ships with the application).
 */
export const diskFilePrefix = "folder:file:", diskViewPrefix = "folder:view:";
/**
 * Whether nothing is created, renamed or removed in the folder of a tab. The id of the folder of a skill says where
 * the skill comes from; the id of a folder of the disk says whether it has one file, or is only read.
 */
export function isReadOnlyTab(tab: FileTab) {
  if (isDiskFolderTab(tab)) return tab.projectId.startsWith(diskFilePrefix) || tab.projectId.startsWith(diskViewPrefix);
  if (!isSkillTab(tab)) return false;
  const source = /^skill:(?:global|project:[^:]+):([A-Za-z]+):/.exec(tab.projectId);
  return !source || skillReadOnly(source[1]);
}
export const fileNodeId = (tab: FileTab) => `file:${fileTabKey(tab)}`;
export const sameFileTab = (a: FileTab | null, b: FileTab | null) => a === b || !!a && !!b && fileTabKey(a) === fileTabKey(b);

/** The project a tab was opened from, while it is still that folder and can be edited. */
export function resolveFileTab(snapshot: WorkspaceSnapshot, tab: FileTab) {
  const projects = snapshot.projects.filter(project => project.id === tab.projectId);
  return projects.length === 1 && projects[0].path === tab.projectPath && !projects[0].archived ? projects[0] : undefined;
}

/**
 * Opens a tab, or activates it when it is already open. At the limit the oldest inactive tab
 * that `keep` does not hold (unsaved edits) is closed; when every tab is held the tab is not opened.
 */
export function openFileTab(state: FileTabs, tab: FileTab, keep: (tab: FileTab) => boolean = () => false): FileTabs {
  const existing = state.open.find(value => sameFileTab(value, tab));
  if (existing) return sameFileTab(state.active, existing) ? state : { ...state, active: existing };
  const open = [...state.open, tab];
  if (open.length > fileTabLimit) {
    const evicted = open.findIndex(value => !sameFileTab(value, state.active) && !sameFileTab(value, tab) && !keep(value));
    if (evicted < 0) return state;
    open.splice(evicted, 1);
  }
  return { open, active: tab, closed: state.closed.filter(value => !sameFileTab(value, tab)) };
}

/** Closes a tab; closing the active one leaves no tab of a project active (the session selection shows again). */
export function closeFileTab(state: FileTabs, tab: FileTab): FileTabs {
  if (!state.open.some(value => sameFileTab(value, tab))) return state;
  return { open: state.open.filter(value => !sameFileTab(value, tab)), active: sameFileTab(state.active, tab) ? null : state.active,
    closed: [...state.closed.filter(value => !sameFileTab(value, tab)), tab].slice(-fileTabLimit) };
}

export function activateFileTab(state: FileTabs, tab: FileTab | null): FileTabs {
  const active = tab ? state.open.find(value => sameFileTab(value, tab)) ?? null : null;
  return active === state.active || tab !== null && !active ? state : { ...state, active };
}

/**
 * Drops tabs whose project is gone, archived or now another folder. Used on restore, never on unsaved edits.
 * The tab of a terminal lasts as long as its terminal: see {@link reconcileTerminalTabs}.
 */
export function reconcileFileTabs(state: FileTabs, snapshot: WorkspaceSnapshot): FileTabs {
  // The folder of a plugin or of a skill is not a project of the workspace: its editor says so itself when the folder is gone.
  // A canvas of the application lasts; one of a project or of a session lasts as long as they are in the space.
  const canvas = (tab: FileTab) => (tab.projectId === "" || resolveFileTab(snapshot, tab)) && (tab.sessionId === undefined || snapshot.sessions.some(session => session.id === tab.sessionId));
  const lasting = (tab: FileTab) => isTerminalTab(tab) || isAutomationsTab(tab) || isWorkItemsTab(tab) || isIssuesTab(tab) || isCanvasesTab(tab) || isFolderTab(tab) || (isCanvasTab(tab) ? canvas(tab) : resolveFileTab(snapshot, tab));
  const open = state.open.filter(lasting);
  const closed = state.closed.filter(lasting);
  const active = open.find(tab => sameFileTab(tab, state.active)) ?? null;
  return open.length === state.open.length && closed.length === state.closed.length && active === state.active ? state : { open, active, closed };
}

/** Drops the tabs of terminals that are gone: a tab that is open, and one that could be reopened. */
export function reconcileTerminalTabs(state: FileTabs, terminals: ReadonlySet<string>): FileTabs {
  const lasting = (tab: FileTab) => !isTerminalTab(tab) || terminals.has(tab.terminalId ?? "");
  if (state.open.every(lasting) && state.closed.every(lasting)) return state;
  const open = state.open.filter(lasting);
  return { open, active: open.find(tab => sameFileTab(tab, state.active)) ?? null, closed: state.closed.filter(lasting) };
}

export type TabKind = "session" | "file";
/** Which kind of tab Reopen restores: the most recently closed kind that still has a closed tab. */
export function reopenTabKind(order: readonly TabKind[], sessionsClosed: number, filesClosed: number): TabKind | null {
  const available = (kind: TabKind) => (kind === "session" ? sessionsClosed : filesClosed) > 0;
  for (let index = order.length - 1; index >= 0; index--) if (available(order[index])) return order[index];
  return available("file") ? "file" : available("session") ? "session" : null;
}

export type TabPosition = Readonly<{ kind: "draft" } | { kind: TabKind; index: number }>;
/** Next/previous tab over one ring: the new-session tab, the session tabs, then the tabs of projects. */
export function cycleTab(sessions: number, files: number, current: TabPosition, delta: 1 | -1): TabPosition {
  const count = 1 + sessions + files;
  const at = current.kind === "draft" ? 0 : current.kind === "session" ? 1 + current.index : 1 + sessions + current.index;
  const next = (at + delta + count) % count;
  return next === 0 ? { kind: "draft" } : next <= sessions ? { kind: "session", index: next - 1 } : { kind: "file", index: next - 1 - sessions };
}

type StoredTab = { projectId?: unknown; projectPath?: unknown; path?: unknown; view?: unknown; name?: unknown; pluginKey?: unknown; canvasId?: unknown; sessionId?: unknown; key?: unknown; icon?: unknown; plugin?: unknown };
const text = (field: unknown, limit: number): field is string => typeof field === "string" && field.length > 0 && field.length <= limit;
const optionalText = (field: unknown, limit: number) => field === undefined || text(field, limit);
// A canvas tab as it was stored: its identity must be well formed; its title, icon and plugin are only looks, and a bad one is left out.
function storedCanvasTab(stored: StoredTab): FileTab | null {
  const project = stored.projectId === "" ? stored.projectPath === "" : text(stored.projectId, 256) && typeof stored.projectPath === "string" && stored.projectPath.length <= 4096;
  if (!project || !text(stored.pluginKey, 512) || typeof stored.canvasId !== "string" || !canvasIdPattern.test(stored.canvasId)
    || !optionalText(stored.sessionId, 128) || !optionalText(stored.key, 128) || stored.path !== undefined) return null;
  return canvasTab({ pluginKey: stored.pluginKey, canvasId: stored.canvasId, sessionId: stored.sessionId as string | undefined, key: stored.key as string | undefined,
    project: stored.projectId === "" ? null : { id: stored.projectId as string, path: stored.projectPath as string } },
    { title: text(stored.name, 200) ? stored.name : null, icon: text(stored.icon, 64) ? stored.icon : null, plugin: text(stored.plugin, 512) ? stored.plugin : null });
}
// A tab as it was stored: of the editor or the changes, or, from before the editor had tabs of its own, of one file.
// Nothing for a tab that is not understood.
function storedTab(value: unknown): { tab: FileTab; file: string | null } | null {
  if (!value || typeof value !== "object") return null;
  const stored = value as StoredTab;
  if (stored.view === "automations") return stored.projectId === "" && stored.projectPath === "" ? { tab: automationsTab, file: null } : null;
  if (stored.view === "workItems") return stored.projectId === "" && stored.projectPath === "" ? { tab: workItemsTab, file: null } : null;
  if (stored.view === "issues") return stored.projectId === "" && stored.projectPath === "" ? { tab: issuesTab, file: null } : null;
  if (stored.view === "canvases") return stored.projectId === "" && stored.projectPath === "" ? { tab: canvasesTab, file: null } : null;
  if (stored.view === "canvas") { const tab = storedCanvasTab(stored); return tab ? { tab, file: null } : null; }
  if (!text(stored.projectId, 256) || !text(stored.projectPath, 4096)) return null;
  const project = { id: stored.projectId, path: stored.projectPath };
  // The host that gave the id of a folder of the disk is gone with the application that stored the tab.
  if (stored.projectId.startsWith(diskFolderPrefix)) return null;
  if (stored.projectId.startsWith(pluginFolderPrefix) || stored.projectId.startsWith(skillFolderPrefix))
    return stored.view === "editor" && stored.path === undefined && text(stored.name, 128) ? { tab: pluginEditorTab({ ...project, name: stored.name }), file: null } : null;
  if (stored.view === "changes" || stored.view === "editor")
    return stored.path === undefined || stored.path === "" ? { tab: stored.view === "changes" ? changesTab(project) : editorTab(project), file: null } : null;
  return stored.view === undefined && text(stored.path, 1024) ? { tab: editorTab(project), file: stored.path } : null;
}

type Restored = { tabs: FileTabs; files: ReadonlyMap<string, readonly string[]> };
function restore(read: () => string | null): Restored | null {
  try {
    const raw = read();
    if (!raw || raw.length > 131072) return null;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object") return null;
    const data = value as { version?: unknown; open?: unknown; active?: unknown };
    if (data.version !== 1 || !Array.isArray(data.open) || data.open.length > fileTabLimit) return null;
    // A tab that is not understood, of a kind that another build stored, is left out: the tabs beside it are restored,
    // and none is active when it was the active one.
    const active = data.active === null ? null : storedTab(data.active);
    const open: FileTab[] = [];
    const files = new Map<string, string[]>();
    for (const entry of data.open.map(storedTab)) {
      if (entry === null) continue;
      // The files that were tabs of their own are now the files of their project's editor.
      if (entry.file !== null) files.set(entry.tab.projectId, [...files.get(entry.tab.projectId) ?? [], entry.file]);
      if (!open.some(tab => sameFileTab(tab, entry.tab))) open.push(entry.tab);
      else if (entry.file === null) return null;
    }
    const selected = active ? open.find(tab => sameFileTab(tab, active.tab)) : null;
    if (selected === undefined) return null;
    return { tabs: { open, active: selected, closed: [] }, files };
  } catch { return null; }
}

export function restoreFileTabs(read: () => string | null): FileTabs | null {
  return restore(read)?.tabs ?? null;
}

/** The files that were stored as tabs of their own, by project: the editor of that project opens them. */
export function restoreLegacyFiles(read: () => string | null): ReadonlyMap<string, readonly string[]> {
  return restore(read)?.files ?? new Map();
}

export function persistFileTabs(write: (value: string) => void, state: FileTabs): boolean {
  try {
    // A terminal does not outlive the application, nor does the id of a folder of the disk: their tabs are not ones to restore.
    const kept = (tab: FileTab) => !isTerminalTab(tab) && !isDiskFolderTab(tab);
    const value = JSON.stringify({ version: 1, open: state.open.filter(kept), active: state.active && kept(state.active) ? state.active : null });
    if (value.length > 131072) return false;
    write(value); return true;
  }
  catch { return false; }
}
