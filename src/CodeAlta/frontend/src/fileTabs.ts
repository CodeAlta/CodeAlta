import type { WorkspaceSnapshot } from "#neoastra";

/**
 * One tab of a project beside the sessions: a project file open in an editor (the project-relative path), or,
 * with `view: "changes"` and no path, the changed files of the project's repository.
 */
export type FileTab = Readonly<{ projectId: string; projectPath: string; path: string; view?: "changes" }>;
export type FileTabs = Readonly<{ open: readonly FileTab[]; active: FileTab | null; closed: readonly FileTab[] }>;
export const fileTabsKey = "codealta.desktop.fileTabs.v1";
export const fileTabLimit = 32;
export const emptyFileTabs = (): FileTabs => ({ open: [], active: null, closed: [] });
// One tab per file of a project, and one for its changes; the path is compared as the host returned it.
export const fileTabKey = (tab: FileTab) => JSON.stringify(tab.view ? [tab.projectId, tab.path, tab.view] : [tab.projectId, tab.path]);
export const isChangesTab = (tab: FileTab) => tab.view === "changes";
/** The tab of a project's changes. */
export const changesTab = (project: Readonly<{ id: string; path: string }>): FileTab => ({ projectId: project.id, projectPath: project.path, path: "", view: "changes" });
export const fileNodeId = (tab: FileTab) => `file:${fileTabKey(tab)}`;
export const sameFileTab = (a: FileTab | null, b: FileTab | null) => a === b || !!a && !!b && fileTabKey(a) === fileTabKey(b);
export const fileTabName = (tab: FileTab) => tab.path.slice(tab.path.lastIndexOf("/") + 1);

/** The project a tab was opened from, while it is still that folder and can be edited. */
export function resolveFileTab(snapshot: WorkspaceSnapshot, tab: FileTab) {
  const projects = snapshot.projects.filter(project => project.id === tab.projectId);
  return projects.length === 1 && projects[0].path === tab.projectPath && !projects[0].archived ? projects[0] : undefined;
}

/**
 * Opens a file, or activates its tab when it is already open. At the limit the oldest inactive tab
 * that `keep` does not hold (unsaved edits) is closed; when every tab is held the file is not opened.
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

/** Closes a tab; closing the active one leaves no file active (the session selection shows again). */
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
/** Next/previous tab over one ring: the new-session tab, the session tabs, then the file tabs. */
export function cycleTab(sessions: number, files: number, current: TabPosition, delta: 1 | -1): TabPosition {
  const count = 1 + sessions + files;
  const at = current.kind === "draft" ? 0 : current.kind === "session" ? 1 + current.index : 1 + sessions + current.index;
  const next = (at + delta + count) % count;
  return next === 0 ? { kind: "draft" } : next <= sessions ? { kind: "session", index: next - 1 } : { kind: "file", index: next - 1 - sessions };
}

export function restoreFileTabs(read: () => string | null): FileTabs | null {
  try {
    const raw = read();
    if (!raw || raw.length > 131072) return null;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object") return null;
    const data = value as { version?: unknown; open?: unknown; active?: unknown };
    if (data.version !== 1 || !Array.isArray(data.open) || data.open.length > fileTabLimit) return null;
    const text = (field: unknown, limit: number) => typeof field === "string" && field.length > 0 && field.length <= limit;
    const valid = (tab: unknown): tab is FileTab => !!tab && typeof tab === "object" && text((tab as FileTab).projectId, 256)
      && text((tab as FileTab).projectPath, 4096) && ((tab as FileTab).view === undefined ? text((tab as FileTab).path, 1024)
        : (tab as FileTab).view === "changes" && (tab as FileTab).path === "");
    if (!data.open.every(valid) || data.active !== null && !valid(data.active)) return null;
    const open = data.open.map((tab): FileTab => tab.view ? changesTab({ id: tab.projectId, path: tab.projectPath })
      : { projectId: tab.projectId, projectPath: tab.projectPath, path: tab.path });
    if (new Set(open.map(fileTabKey)).size !== open.length) return null;
    const active = data.active === null ? null : open.find(tab => sameFileTab(tab, data.active as FileTab));
    if (active === undefined) return null;
    return { open, active, closed: [] };
  } catch { return null; }
}

export function persistFileTabs(write: (value: string) => void, state: FileTabs): boolean {
  try {
    const value = JSON.stringify({ version: 1, open: state.open, active: state.active });
    if (value.length > 131072) return false;
    write(value); return true;
  }
  catch { return false; }
}
