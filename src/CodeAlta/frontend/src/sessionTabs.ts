import type { WorkspaceSnapshot } from "#neoastra";

export type SessionTab = Readonly<{ sessionId: string; projectId: string | null; path: string | null }>;
/** The tab of the prompt of a new session of a scope: a project with its folder, or the chats (no project, no folder). */
export type DraftTab = Readonly<{ projectId: string | null; path: string | null }>;
/**
 * The tabs of sessions of a window. One of the open tabs can be the transient one (`preview`): the tab a session is
 * looked at in, which the next session looked at takes. `drafts` are the scopes whose new-session prompt has a tab of
 * its own, which stays; the prompt of the selected scope is shown without one, in a tab that is transient too.
 */
export type SessionTabs = Readonly<{ open: readonly SessionTab[]; active: SessionTab | null; closed: readonly SessionTab[];
  preview?: SessionTab | null; drafts?: readonly DraftTab[] }>;
/** How a session is opened: to look at it, in the transient tab, or to keep it, in a tab of its own. */
export type SessionTabMode = "preview" | "permanent";
export const sessionTabsKey = "codealta.desktop.sessionTabs.v1";
export const sessionTabLimit = 32;
export const draftTabLimit = 16;
export const emptySessionTabs = (): SessionTabs => ({ open: [], active: null, closed: [] });
export const tabKey = (tab: SessionTab) => JSON.stringify([tab.projectId, tab.sessionId, tab.path]);
export const draftKey = (draft: DraftTab) => JSON.stringify([draft.projectId, draft.path]);
const same = (a: SessionTab | null | undefined, b: SessionTab | null | undefined) => a === b || !!a && !!b && tabKey(a) === tabKey(b);
const sameDraft = (a: DraftTab | null | undefined, b: DraftTab | null | undefined) => a === b || !!a && !!b && draftKey(a) === draftKey(b);
const noDrafts: readonly DraftTab[] = [];
// A state that never had a transient tab keeps the shape it had: the key is written once there is something to say.
const previewOf = (state: SessionTabs, preview: SessionTab | null) => same(preview, state.preview ?? null) && (preview !== null || state.preview === undefined) ? {} : { preview };

/** Whether a tab is the transient one. */
export const isPreviewTab = (state: SessionTabs, tab: SessionTab | null | undefined) => !!state.preview && same(state.preview, tab);
/** Whether the new-session prompt of a scope has a tab of its own. */
export const hasDraftTab = (state: SessionTabs, draft: DraftTab | null | undefined) => !!draft && (state.drafts ?? noDrafts).some(value => sameDraft(value, draft));

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

/** The scope a new session is drafted in: the project of the catalog with that id, or the chats for none. Null for a project the catalog does not have once. */
export function draftTabOf(snapshot: WorkspaceSnapshot, projectId: string | null): DraftTab | null {
  if (projectId === null) return { projectId: null, path: null };
  const projects = snapshot.projects.filter(project => project.id === projectId);
  return projects.length === 1 ? { projectId, path: projects[0].path } : null;
}

/** Whether the scope of a draft tab is still what the catalog says: its project, in the same folder. */
export function resolveDraftTab(snapshot: WorkspaceSnapshot, draft: DraftTab): boolean {
  return sameDraft(draftTabOf(snapshot, draft.projectId), draft);
}

/**
 * What the pane of a session is mounted under, and what its owners are kept by: another host, another session,
 * or the same session after a rewind, is another pane. A rewind writes the journal of the session again, so
 * everything the pane read of it (its rows, their offsets, where it was scrolled) is read anew.
 */
export function sessionPaneKey(hostEpoch: string | null | undefined, tab: SessionTab, createdAt: string | null, rewinds: number): string {
  return JSON.stringify([hostEpoch, tabKey(tab), createdAt, rewinds]);
}

export function selectedTab(snapshot: WorkspaceSnapshot, projectId: string | null, sessionId: string | null): SessionTab | null {
  const row = snapshot.sessions.find(row => row.id === sessionId);
  if (!row) return null;
  const tab = { projectId, sessionId: row.id, path: row.workspacePath };
  return resolveSessionTab(snapshot, tab) ? tab : null;
}

/**
 * Opens the tab of a session and shows it. A session that has a tab keeps it, and keeps what it is: looking at a
 * session that was kept does not make its tab transient again, and keeping (`permanent`) the one of the transient tab
 * makes that tab its own. A session that has none takes the transient tab when it is looked at (`preview`), in the
 * place of the session that was there, which is not listed among the closed tabs: it was never kept.
 */
export function openSessionTab(state: SessionTabs, tab: SessionTab, mode: SessionTabMode = "permanent"): SessionTabs {
  if (state.open.some(value => same(value, tab))) {
    const kept = mode === "permanent" && isPreviewTab(state, tab);
    return same(state.active, tab) && !kept ? state : { ...state, active: tab, ...(kept ? { preview: null } : {}) };
  }
  const closed = state.closed.filter(value => !same(value, tab));
  if (mode === "preview") {
    const at = state.preview ? state.open.findIndex(value => same(value, state.preview)) : -1;
    if (at >= 0) return { ...state, open: state.open.map((value, index) => index === at ? tab : value), active: tab, closed, preview: tab };
  }
  // Bounded presentation only: evict the oldest inactive tab, never its draft/request.
  const open = [...state.open, tab];
  let preview = mode === "preview" ? tab : state.preview ?? null;
  if (open.length > sessionTabLimit) {
    const [evicted] = open.splice(open.findIndex(value => !same(value, state.active)), 1);
    if (same(evicted, preview)) preview = null;
  }
  return { ...state, open, active: tab, closed, ...previewOf(state, preview) };
}

/** Keeps the session of the transient tab: its tab is its own from now on. The same state when the tab is not the transient one. */
export function pinSessionTab(state: SessionTabs, tab: SessionTab): SessionTabs {
  return isPreviewTab(state, tab) ? { ...state, preview: null } : state;
}

export function closeSessionTab(state: SessionTabs, tab: SessionTab): SessionTabs {
  if (!state.open.some(value => same(value, tab))) return state;
  const open = state.open.filter(value => !same(value, tab));
  return { ...state, open, active: same(state.active, tab) ? open[0] ?? null : state.active,
    closed: [...state.closed.filter(value => !same(value, tab)), tab].slice(-sessionTabLimit),
    ...previewOf(state, isPreviewTab(state, tab) ? null : state.preview ?? null) };
}

/**
 * Takes the session of the transient tab away, for what takes its place (the prompt of a new session). It was only
 * looked at: it is not listed among the closed tabs. When it was the session shown, none is.
 */
export function closePreviewTab(state: SessionTabs): SessionTabs {
  const preview = state.preview;
  if (!preview || !state.open.some(value => same(value, preview))) return state.preview ? { ...state, preview: null } : state;
  return { ...state, open: state.open.filter(value => !same(value, preview)), active: same(state.active, preview) ? null : state.active, preview: null };
}

/** Gives the new-session prompt of a scope a tab of its own. The oldest of them leaves when there are too many. */
export function openDraftTab(state: SessionTabs, draft: DraftTab): SessionTabs {
  if (hasDraftTab(state, draft)) return state;
  return { ...state, drafts: [...state.drafts ?? noDrafts, draft].slice(-draftTabLimit) };
}

export function closeDraftTab(state: SessionTabs, draft: DraftTab): SessionTabs {
  return hasDraftTab(state, draft) ? { ...state, drafts: (state.drafts ?? noDrafts).filter(value => !sameDraft(value, draft)) } : state;
}

export function reconcileSessionTabs(state: SessionTabs, snapshot: WorkspaceSnapshot): SessionTabs {
  const open = state.open.filter(tab => resolveSessionTab(snapshot, tab));
  const closed = state.closed.filter(tab => resolveSessionTab(snapshot, tab));
  const active = open.find(tab => same(tab, state.active)) ?? (state.active ? open[0] ?? null : null);
  const preview = open.find(tab => same(tab, state.preview)) ?? null;
  const drafts = (state.drafts ?? noDrafts).filter(draft => resolveDraftTab(snapshot, draft));
  return open.length === state.open.length && closed.length === state.closed.length && same(active, state.active)
    && same(preview, state.preview ?? null) && drafts.length === (state.drafts ?? noDrafts).length
    ? state : { open, active, closed, ...(state.preview === undefined ? {} : { preview }), ...(state.drafts === undefined ? {} : { drafts }) };
}

export function restoreSessionTabs(read: () => string | null): SessionTabs | null {
  try {
    const raw = read();
    if (!raw || raw.length > 65536) return null;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object") return null;
    const data = value as { version?: unknown; open?: unknown; active?: unknown; preview?: unknown; drafts?: unknown };
    if (data.version !== 1 || !Array.isArray(data.open) || data.open.length > sessionTabLimit) return null;
    const scope = (t: DraftTab) => (t.projectId === null || typeof t.projectId === "string" && t.projectId.length > 0 && t.projectId.length <= 256) &&
      (t.path === null || typeof t.path === "string" && t.path.length <= 4096);
    const valid = (tab: unknown): tab is SessionTab => {
      if (!tab || typeof tab !== "object") return false;
      const t = tab as SessionTab;
      return typeof t.sessionId === "string" && t.sessionId.length > 0 && t.sessionId.length <= 256 && scope(t);
    };
    if (!data.open.every(valid) || data.active !== null && !valid(data.active)) return null;
    const open = data.open.map(tab => ({ sessionId: tab.sessionId, projectId: tab.projectId, path: tab.path }));
    if (new Set(open.map(tabKey)).size !== open.length) return null;
    const active = data.active === null ? null : open.find(tab => same(tab, data.active as SessionTab));
    if (active === undefined) return null;
    // What an older build stored has neither a transient tab nor tabs of prompts; one that is not understood is left out.
    const preview = valid(data.preview) ? open.find(tab => same(tab, data.preview as SessionTab)) ?? null : null;
    const drafts: DraftTab[] = [];
    if (Array.isArray(data.drafts)) for (const draft of data.drafts.slice(0, draftTabLimit)) {
      if (!draft || typeof draft !== "object" || !scope(draft as DraftTab)) continue;
      const kept = { projectId: (draft as DraftTab).projectId, path: (draft as DraftTab).path };
      if (!drafts.some(other => sameDraft(other, kept))) drafts.push(kept);
    }
    return { open, active, closed: [], ...(preview ? { preview } : {}), ...(drafts.length ? { drafts } : {}) };
  } catch { return null; }
}

export function persistSessionTabs(write: (value: string) => void, state: SessionTabs): boolean {
  try {
    const value = JSON.stringify({ version: 1, open: state.open, active: state.active, ...(state.preview ? { preview: state.preview } : {}), ...(state.drafts?.length ? { drafts: state.drafts } : {}) });
    if (value.length > 65536) return false;
    write(value); return true;
  }
  catch { return false; }
}
