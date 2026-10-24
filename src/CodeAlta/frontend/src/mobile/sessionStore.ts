import { isBusy, type Live, type Session, type SessionPage } from "./contract";

/** A session as its card and its page show it: what the server said of it, with its latest live state. */
export type SessionView = Readonly<{
  id: string;
  title: string | null;
  projectId: string | null;
  project: string | null;
  summary: string | null;
  lastActivity: string | null;
  live: Live | null;
}>;

/** The server pages six sessions at a time, and fifty at most. */
export const pageSize = 6;
export const maximumPageSize = 50;

/** What a `session` event asks of the page: draw the card again, read the list again, or nothing. */
export type LiveDecision = "listed" | "reload" | "none";

const time = (iso: string | null | undefined) => { const value = Date.parse(iso ?? ""); return Number.isFinite(value) ? value : 0; };

/** The time a session was last active, for the order of the list and the color of its dot. */
export const lastActive = (view: SessionView): number => Math.max(time(view.live?.at), time(view.lastActivity));

/**
 * The sessions the page knows, with no DOM: the list as the server paged it (within the project filter), the sessions
 * read alone, and the live state of every session the event stream told of, listed or not.
 *
 * A live update newer than a page wins over that page: each update is stamped, a read of the list notes the stamp it
 * started at, and its sessions only replace live states that were not updated since.
 */
export function createSessionStore() {
  const known = new Map<string, Session>();
  const live = new Map<string, Readonly<{ live: Live; stamp: number }>>();
  let stamp = 0;
  let listed: string[] = [];
  let next = 0, hasMore = false, filter = "";
  // Sessions out of the list that already made the list be read again: once is enough for each.
  const requested = new Set<string>();
  // Sessions swiped away, kept out of the list while their archive settles (and until a read no longer lists them).
  const gone = new Set<string>(), settling = new Set<string>();

  const view = (id: string): SessionView | null => {
    const session = known.get(id), state = live.get(id)?.live ?? null;
    if (!session && !state) return null;
    return {
      id, title: session?.title ?? null, projectId: session?.projectId ?? state?.projectId ?? null, project: session?.project ?? null,
      summary: session?.summary ?? null, lastActivity: session?.lastActivity ?? null, live: state,
    };
  };

  // Whether reading the list again could show this session: it works now, the list is not full, or it is newer
  // than the oldest card. A session that was active long ago and is not listed stays out (the stream tells of every
  // session seen since the plugin started, on each connection).
  const couldShow = (update: Live) => {
    if (isBusy(update.live) || !hasMore || listed.length === 0) return true;
    const oldest = Math.min(...listed.map(id => { const v = view(id); return v ? lastActive(v) : 0; }));
    return time(update.at) > oldest;
  };

  return {
    /** The project the list is filtered on; "" for every project. */
    get filter() { return filter; },
    get hasMore() { return hasMore; },
    get next() { return next; },
    /** How many sessions the list holds, shown or not. */
    get count() { return listed.length; },
    /** The stamp a read of the list starts at. */
    beginRead: () => stamp,
    /** Takes a page of the list read from `startedAt`: the first page replaces the list, a later one adds to it. */
    applyPage(page: SessionPage, startedAt: number, append: boolean) {
      const ids: string[] = [];
      for (const session of page.sessions ?? []) {
        known.set(session.id, session);
        const current = live.get(session.id);
        if (session.live && (!current || current.stamp <= startedAt)) live.set(session.id, { live: session.live, stamp: startedAt });
        ids.push(session.id);
      }
      listed = append ? [...listed, ...ids.filter(id => !listed.includes(id))] : ids;
      next = page.next ?? listed.length;
      hasMore = !!page.hasMore;
      // An archive that settled is out of the server's list: it no longer needs to be hidden.
      for (const id of [...gone]) if (!settling.has(id) && !listed.includes(id)) gone.delete(id);
    },
    /** Takes a session read alone. */
    applySession(session: Session) {
      known.set(session.id, session);
      if (session.live && !live.has(session.id)) live.set(session.id, { live: session.live, stamp });
    },
    /** Takes a live update of the event stream, and says what the page should do about it. */
    applyLive(update: Live): LiveDecision {
      const before = live.get(update.id)?.live;
      // A state without a line keeps the line it had: the card keeps saying what was last done.
      const merged: Live = { ...update, line: update.line ?? before?.line ?? null, at: update.at ?? before?.at ?? null,
        projectId: update.projectId ?? before?.projectId ?? known.get(update.id)?.projectId ?? null };
      live.set(update.id, { live: merged, stamp: ++stamp });
      if (listed.includes(update.id)) return "listed";
      if (requested.has(update.id)) return "none";
      if (filter && merged.projectId !== filter) return "none";
      if (!couldShow(merged)) return "none";
      requested.add(update.id);
      return "reload";
    },
    /** A session as the page shows it, or null when nothing is known of it. */
    view,
    /** The cards of the list, the latest active first, without those swiped away. */
    list(): SessionView[] {
      return listed.filter(id => !gone.has(id)).map(id => view(id)!).filter(Boolean).sort((a, b) => lastActive(b) - lastActive(a));
    },
    /** Whether the list holds a session. */
    listed: (id: string) => listed.includes(id),
    /** Filters the list on a project ("" for all): the list is empty until it is read again. */
    setFilter(project: string) {
      filter = project; listed = []; next = 0; hasMore = false; requested.clear();
    },
    /** Hides a session while its archive is asked for. */
    hide(id: string) { gone.add(id); settling.add(id); },
    /** The archive of a session was done: it stays hidden until the server no longer lists it. */
    settled(id: string) { settling.delete(id); },
    /** Shows a session again (its archive failed, or it was restored). */
    bringBack(id: string) { gone.delete(id); settling.delete(id); },
    /** Whether a session is hidden. */
    hidden: (id: string) => gone.has(id),
  };
}

/** The sessions of the page. */
export type SessionStore = ReturnType<typeof createSessionStore>;

/** How many sessions a read of the whole list asks for: what is shown, so that pages already shown stay. */
export function readSizes(count: number): number[] {
  const wanted = Math.max(pageSize, count);
  const sizes: number[] = [];
  for (let left = wanted; left > 0; left -= maximumPageSize) sizes.push(Math.min(maximumPageSize, left));
  return sizes;
}

/** The dot of a card: the live state while it works, then "answered" for an hour, then "inactive". */
export type Dot = "running" | "tools" | "thinking" | "answered" | "inactive";
export const hour = 3600_000;
export function dotOf(view: SessionView, now: number): Dot {
  const state = view.live?.live ?? "idle";
  if (state !== "idle") return state;
  const at = lastActive(view);
  return at && now - at < hour ? "answered" : "inactive";
}
export const dotLabels: Readonly<Record<Dot, string>> = { running: "working", tools: "tools", thinking: "thinking", answered: "done", inactive: "inactive" };

/** "just now", "5 min ago", "3 h ago", "2 d ago". */
export function ago(iso: string | null | undefined, now: number): string {
  const at = time(iso);
  if (!at) return "";
  const seconds = Math.max(0, (now - at) / 1000);
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  return `${Math.floor(seconds / 86400)} d ago`;
}
