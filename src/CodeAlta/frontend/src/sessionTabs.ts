import type { WorkspaceSnapshot } from "#neoastra";

export type SessionTab = Readonly<{ sessionId: string; projectId: string | null; path: string | null }>;
export type SessionTabs = Readonly<{ open: readonly SessionTab[]; active: SessionTab | null; closed: readonly SessionTab[] }>;
export const sessionTabsKey = "codealta.desktop.sessionTabs.v1";
export const sessionTabLimit = 32;
export const emptySessionTabs = (): SessionTabs => ({ open: [], active: null, closed: [] });
export const tabKey = (tab: SessionTab) => JSON.stringify([tab.projectId, tab.sessionId, tab.path]);
const same = (a: SessionTab | null, b: SessionTab | null) => a === b || !!a && !!b && tabKey(a) === tabKey(b);

// Session stores are keyed by session ID, so duplicates across ANY scope refuse.
// A path or scope mismatch must never redirect a restored presentation identity.
export function resolveSessionTab(snapshot: WorkspaceSnapshot, tab: SessionTab) {
  const rows = snapshot.sessions.filter(row => row.id === tab.sessionId);
  if (rows.length !== 1) return undefined;
  const row = rows[0];
  if (row.workspacePath !== tab.path) return undefined;
  if (tab.projectId === null) return row.scopeKind === "global" && row.projectId === null ? row : undefined;
  const projects = snapshot.projects.filter(project => project.id === tab.projectId);
  return projects.length === 1 && projects[0].path === tab.path && row.scopeKind === "project" && row.projectId === tab.projectId
    ? row : undefined;
}

export function selectedTab(snapshot: WorkspaceSnapshot, projectId: string | null, sessionId: string | null): SessionTab | null {
  const row = snapshot.sessions.find(row => row.id === sessionId);
  if (!row) return null;
  const tab = { projectId, sessionId: row.id, path: row.workspacePath };
  return resolveSessionTab(snapshot, tab) ? tab : null;
}

export function openSessionTab(state: SessionTabs, tab: SessionTab): SessionTabs {
  if (state.open.some(value => same(value, tab))) return same(state.active, tab) ? state : { ...state, active: tab };
  // Bounded presentation only: evict the oldest inactive tab, never its draft/request.
  const open = [...state.open, tab];
  if (open.length > sessionTabLimit) open.splice(open.findIndex(value => !same(value, state.active)), 1);
  return { open, active: tab, closed: state.closed.filter(value => !same(value, tab)) };
}

export function closeSessionTab(state: SessionTabs, tab: SessionTab): SessionTabs {
  if (!state.open.some(value => same(value, tab))) return state;
  const open = state.open.filter(value => !same(value, tab));
  return { open, active: same(state.active, tab) ? open[0] ?? null : state.active,
    closed: [...state.closed.filter(value => !same(value, tab)), tab].slice(-sessionTabLimit) };
}

export function reconcileSessionTabs(state: SessionTabs, snapshot: WorkspaceSnapshot): SessionTabs {
  const open = state.open.filter(tab => resolveSessionTab(snapshot, tab));
  const closed = state.closed.filter(tab => resolveSessionTab(snapshot, tab));
  const active = open.find(tab => same(tab, state.active)) ?? (state.active ? open[0] ?? null : null);
  return open.length === state.open.length && closed.length === state.closed.length && same(active, state.active)
    ? state : { open, active, closed };
}

export function restoreSessionTabs(read: () => string | null): SessionTabs | null {
  try {
    const raw = read();
    if (!raw || raw.length > 65536) return null;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object") return null;
    const data = value as { version?: unknown; open?: unknown; active?: unknown };
    if (data.version !== 1 || !Array.isArray(data.open) || data.open.length > sessionTabLimit) return null;
    const valid = (tab: unknown): tab is SessionTab => {
      if (!tab || typeof tab !== "object") return false;
      const t = tab as SessionTab;
      return typeof t.sessionId === "string" && t.sessionId.length > 0 && t.sessionId.length <= 256 &&
        (t.projectId === null || typeof t.projectId === "string" && t.projectId.length > 0 && t.projectId.length <= 256) &&
        (t.path === null || typeof t.path === "string" && t.path.length <= 4096);
    };
    if (!data.open.every(valid) || data.active !== null && !valid(data.active)) return null;
    const open = data.open.map(tab => ({ sessionId: tab.sessionId, projectId: tab.projectId, path: tab.path }));
    if (new Set(open.map(tabKey)).size !== open.length) return null;
    const active = data.active === null ? null : open.find(tab => same(tab, data.active as SessionTab));
    if (active === undefined) return null;
    return { open, active, closed: [] };
  } catch { return null; }
}

export function persistSessionTabs(write: (value: string) => void, state: SessionTabs): boolean {
  try {
    const value = JSON.stringify({ version: 1, open: state.open, active: state.active });
    if (value.length > 65536) return false;
    write(value); return true;
  }
  catch { return false; }
}
