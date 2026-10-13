import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { BookmarkRow } from "#neoastra";
import { BookmarksPanel } from "./BookmarksPanel";
import { createBookmarksHub, type BookmarksApi } from "./bookmarksHub";

// The tab of the bookmarks over a host that keeps a few: two projects, a chat, a session that no longer exists.
let serial = 0;
const row = (values: Partial<BookmarkRow>): BookmarkRow => ({ id: `b${++serial}`, kind: "message", sessionId: "s", projectId: "alpha", sessionTitle: "Session", sessionMissing: false, role: "assistant",
  label: null, excerpt: "Text.", runId: "run", contentId: `c${serial}`, offset: "100", messageAt: "2026-10-09T11:54:00Z", createdAt: "2026-10-10T08:00:00Z", createdBy: null, ...values });
const state = {
  rows: [
    row({ id: "release", sessionId: "ship", sessionTitle: "Ship the release", role: "assistant", excerpt: "**Both entries are implemented** and checked: the tag triggers the release, and the package is on the feed a few minutes later.", messageAt: "2026-10-09T11:54:00Z", createdAt: "2026-10-12T09:00:00Z" }),
    row({ id: "prompt", sessionId: "ship", sessionTitle: "Ship the release", role: "user", excerpt: "Check the two entries of the changelog.", messageAt: "2026-10-09T11:40:00Z", createdAt: "2026-10-12T08:00:00Z" }),
    row({ id: "whole", kind: "session", role: null, excerpt: null, sessionId: "design", sessionTitle: "Design review", messageAt: null, createdAt: "2026-10-11T08:00:00Z" }),
    row({ id: "idea", sessionId: "chat", projectId: null, sessionTitle: "Ideas", label: "The naming idea", excerpt: "Call it a rail.", createdAt: "2026-10-08T08:00:00Z" }),
    row({ id: "lost", sessionId: "gone", projectId: "beta", sessionTitle: "Removed session", sessionMissing: true, excerpt: "What was said.", createdAt: "2026-10-01T08:00:00Z" }),
  ] as BookmarkRow[],
  status: "ok", notify: [] as (() => void)[], gone: [] as string[], opened: [] as string[], notices: [] as string[], renames: [] as [string, string | null][], refuse: false,
};
const changed = () => { for (const tell of state.notify) tell(); };
const api = {
  list: async () => state.status === "ok" ? { status: "ok", bookmarks: [...state.rows], truncated: false } : { status: state.status, bookmarks: [], truncated: false },
  add: async () => ({ status: "invalid_request", bookmark: null }),
  rename: async (request: { id: string; label?: string | null }) => {
    state.renames.push([request.id, request.label ?? null]);
    const index = state.rows.findIndex(candidate => candidate.id === request.id);
    if (state.refuse || index < 0) return { status: "write_failed", bookmark: null };
    state.rows[index] = { ...state.rows[index], label: request.label ?? null }; changed();
    return { status: "ok", bookmark: state.rows[index] };
  },
  remove: async (request: { id: string }) => {
    const found = state.rows.find(candidate => candidate.id === request.id);
    if (state.refuse || !found) return { status: "write_failed", bookmark: null };
    state.rows = state.rows.filter(candidate => candidate !== found); changed();
    return { status: "ok", bookmark: found };
  },
  resolve: async () => ({ status: "ok", sessionId: null, projectId: null, offset: null }),
  watch: async (_request: unknown, options?: { signal?: AbortSignal }) => {
    const pending: number[] = [0];
    let wake: (() => void) | null = null, ended = false, count = 0;
    const tell = () => { pending.push(++count); wake?.(); };
    state.notify.push(tell);
    options?.signal?.addEventListener("abort", () => { ended = true; wake?.(); });
    return (async function* () {
      for (;;) {
        while (pending.length) yield { revision: pending.shift()! };
        if (ended) return;
        await new Promise<void>(resolve => { wake = resolve; });
      }
    })();
  },
} as unknown as BookmarksApi;

const hub = createBookmarksHub(api);
let disconnect = hub.connect("epoch");
const projects = [{ id: "alpha", name: "Alpha" }, { id: "beta", name: "Beta" }];
Object.assign(window, { bookmarksFixture: { state, changed,
  /** The host answers something else from now on, and the tab reads it. */
  restart(status: string, rows?: BookmarkRow[]) { state.status = status; if (rows) state.rows = rows; disconnect(); disconnect = hub.connect("epoch"); } } });

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell" style={{ height: "100%" }}>
  <BookmarksPanel hub={hub} projects={projects} onGo={bookmark => { state.gone.push(bookmark.id); }} onOpenSession={id => { state.opened.push(id); }}
    onNotice={message => { state.notices.push(message); }} />
</div></StrictMode>);
