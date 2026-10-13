// The page's link to the bookmarks of the application: what they are now, and the calls that change them.
import type { bookmarks, BookmarkRow } from "#neoastra";

export type BookmarksApi = Pick<typeof bookmarks, "list" | "add" | "rename" | "remove" | "resolve" | "watch">;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** What the page shows of the bookmarks. `loaded` is false until the host answered once. */
export type BookmarksState = Readonly<{ loaded: boolean; available: boolean; bookmarks: readonly BookmarkRow[]; truncated: boolean }>;
/** How a change ended: done, with the bookmark as it is now, or refused with what the host said. */
export type BookmarkOutcome = Readonly<{ ok: boolean; status: string; bookmark: BookmarkRow | null }>;
/** Where a bookmark leads: its session, and for a message the offset its journal holds it at now. */
export type BookmarkTarget = Readonly<{ status: string; sessionId: string | null; projectId: string | null; offset: string | null }>;

const empty: BookmarksState = { loaded: false, available: false, bookmarks: [], truncated: false };
const failed: BookmarkOutcome = { ok: false, status: "failed", bookmark: null };
/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;

export type BookmarksHub = ReturnType<typeof createBookmarksHub>;

/**
 * Keeps what the host says of the bookmarks. It reads them once it is connected and each time the host says
 * something changed: a bookmark an agent added is shown as one the user added. A change made here is shown at
 * once, before the host tells of it.
 */
export function createBookmarksHub(api: BookmarksApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let state = empty;
  let epoch: string | null = null;
  let reading = 0;
  const listeners = new Set<() => void>();
  const publish = (next: BookmarksState) => {
    // The same answer again changes nothing on the page.
    if (JSON.stringify(next) === JSON.stringify(state)) return;
    state = next;
    for (const listener of [...listeners]) listener();
  };

  async function read() {
    const host = epoch;
    if (!host) return;
    const turn = ++reading;
    try {
      const reply = await api.list({ expectedEpoch: host }, { timeoutMilliseconds: 30_000 });
      if (epoch !== host || turn !== reading) return;
      publish(reply.status === "ok" ? { loaded: true, available: true, bookmarks: reply.bookmarks, truncated: reply.truncated }
        : { ...empty, loaded: true });
    } catch { /* The host is gone: the next connection reads again. */ }
  }

  // What a change did, shown before the host lists again; the list that follows says what there really is.
  function apply(change: (bookmarks: readonly BookmarkRow[]) => readonly BookmarkRow[]) {
    if (state.available) publish({ ...state, bookmarks: change(state.bookmarks) });
  }

  async function change(call: (host: string) => Promise<{ status: string; bookmark?: BookmarkRow | null }>, shown: (bookmark: BookmarkRow) => void): Promise<BookmarkOutcome> {
    const host = epoch;
    if (!host) return failed;
    try {
      const reply = await call(host);
      if (epoch !== host) return failed;
      const bookmark = reply.bookmark ?? null;
      if (reply.status === "ok" && bookmark) { reading++; shown(bookmark); }
      void read();
      return { ok: reply.status === "ok", status: reply.status, bookmark };
    } catch { return failed; }
  }

  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    /** Listens to one host until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      epoch = hostEpoch;
      publish(empty);
      async function watch() {
        try {
          // The first thing the host tells is that it listens: the list is read then, and at each change.
          for await (const _ of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            void read();
          }
        } catch { /* Told again below. */ }
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void read();
      void watch();
      return () => {
        abort.abort();
        if (timer !== null) timers.clear(timer);
        if (epoch === hostEpoch) { epoch = null; reading++; publish(empty); }
      };
    },
    /** Reads the bookmarks again. */
    refresh: read,
    /** Bookmarks a session, or with an offset the message its journal holds there. */
    add: (sessionId: string, offset: string | null = null, label: string | null = null) =>
      change(host => api.add({ expectedEpoch: host, sessionId, offset, label }, { timeoutMilliseconds: 15_000 }),
        bookmark => apply(bookmarks => bookmarks.some(other => other.id === bookmark.id) ? bookmarks : [bookmark, ...bookmarks])),
    /** Names a bookmark; nothing gives it back the name of what it marks. */
    rename: (id: string, label: string | null) =>
      change(host => api.rename({ expectedEpoch: host, id, label }, { timeoutMilliseconds: 15_000 }),
        bookmark => apply(bookmarks => bookmarks.map(other => other.id === bookmark.id ? bookmark : other))),
    remove: (id: string) =>
      change(host => api.remove({ expectedEpoch: host, id }, { timeoutMilliseconds: 15_000 }),
        () => apply(bookmarks => bookmarks.filter(other => other.id !== id))),
    /** Where a bookmark leads now. */
    async resolve(id: string, signal?: AbortSignal): Promise<BookmarkTarget> {
      const host = epoch;
      if (!host) return { status: "unavailable", sessionId: null, projectId: null, offset: null };
      try {
        const reply = await api.resolve({ expectedEpoch: host, id }, { signal, timeoutMilliseconds: 30_000 });
        return { status: reply.status, sessionId: reply.sessionId ?? null, projectId: reply.projectId ?? null, offset: reply.offset ?? null };
      } catch { return { status: "failed", sessionId: null, projectId: null, offset: null }; }
    },
  };
}
