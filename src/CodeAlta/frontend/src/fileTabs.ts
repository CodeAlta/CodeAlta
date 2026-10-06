import type { WorkspaceSnapshot } from "#neoastra";

/**
 * One tab of a project beside the sessions: its code editor (`view: "editor"`, with the files opened in it as
 * tabs of its own) or the changed files of its repository (`view: "changes"`). A project has one of each at most.
 */
export type FileTab = Readonly<{ projectId: string; projectPath: string; view: "editor" | "changes" }>;
export type FileTabs = Readonly<{ open: readonly FileTab[]; active: FileTab | null; closed: readonly FileTab[] }>;
export const fileTabsKey = "codealta.desktop.fileTabs.v1";
export const fileTabLimit = 32;
export const emptyFileTabs = (): FileTabs => ({ open: [], active: null, closed: [] });
export const fileTabKey = (tab: FileTab) => JSON.stringify([tab.projectId, tab.view]);
export const isChangesTab = (tab: FileTab) => tab.view === "changes";
export const isEditorTab = (tab: FileTab) => tab.view === "editor";
/** The tab of a project's changes. */
export const changesTab = (project: Readonly<{ id: string; path: string }>): FileTab => ({ projectId: project.id, projectPath: project.path, view: "changes" });
/** The tab of a project's code editor. */
export const editorTab = (project: Readonly<{ id: string; path: string }>): FileTab => ({ projectId: project.id, projectPath: project.path, view: "editor" });
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

/** Drops tabs whose project is gone, archived or now another folder. Used on restore, never on unsaved edits. */
export function reconcileFileTabs(state: FileTabs, snapshot: WorkspaceSnapshot): FileTabs {
  const open = state.open.filter(tab => resolveFileTab(snapshot, tab));
  const closed = state.closed.filter(tab => resolveFileTab(snapshot, tab));
  const active = open.find(tab => sameFileTab(tab, state.active)) ?? null;
  return open.length === state.open.length && closed.length === state.closed.length && active === state.active ? state : { open, active, closed };
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

type StoredTab = { projectId?: unknown; projectPath?: unknown; path?: unknown; view?: unknown };
const text = (field: unknown, limit: number): field is string => typeof field === "string" && field.length > 0 && field.length <= limit;
// A tab as it was stored: of the editor or the changes, or, from before the editor had tabs of its own, of one file.
function storedTab(value: unknown): { tab: FileTab; file: string | null } | null {
  if (!value || typeof value !== "object") return null;
  const stored = value as StoredTab;
  if (!text(stored.projectId, 256) || !text(stored.projectPath, 4096)) return null;
  const project = { id: stored.projectId, path: stored.projectPath };
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
    const stored = data.open.map(storedTab);
    const active = data.active === null ? null : storedTab(data.active);
    if (stored.some(entry => entry === null) || active === null && data.active !== null) return null;
    const open: FileTab[] = [];
    const files = new Map<string, string[]>();
    for (const entry of stored) {
      // The files that were tabs of their own are now the files of their project's editor.
      if (entry!.file !== null) files.set(entry!.tab.projectId, [...files.get(entry!.tab.projectId) ?? [], entry!.file]);
      if (!open.some(tab => sameFileTab(tab, entry!.tab))) open.push(entry!.tab);
      else if (entry!.file === null) return null;
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
    const value = JSON.stringify({ version: 1, open: state.open, active: state.active });
    if (value.length > 131072) return false;
    write(value); return true;
  }
  catch { return false; }
}
