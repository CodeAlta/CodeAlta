// The page's link to the spaces of the catalog: what they are now, what their sessions do, and the calls that change them.
import type { spaces as spacesService, SpaceChangeResponse, SpaceSessionActivity } from "#neoastra";
import { defaultSpace, readSessionActivity, readSpaces, sameSessions, sameSpaces, spaceActivities, type Space, type SpaceActivity } from "./spaces";

export type SpacesApi = Pick<typeof spacesService, "list" | "create" | "update" | "delete" | "assign" | "reorder" | "shown" | "activity" | "watch">;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** What the page shows of the spaces. `loaded` is false until the host answered once. */
export type SpacesState = Readonly<{
  loaded: boolean;
  /** Whether the host keeps spaces: a window without a host of its own has the default space alone. */
  available: boolean;
  spaces: readonly Space[];
  /** The sessions that run, work in the background, failed or wait for the user, in any space. */
  sessions: readonly SpaceSessionActivity[];
  /** What the sessions of each space are doing, by the identifier of the space. */
  activity: ReadonlyMap<string, SpaceActivity>;
}>;

/** How a change ended: done, or refused with what the host said. */
export type SpaceOutcome = Readonly<{ ok: boolean; status: string; message: string | null; space: Space | null }>;

const noActivity: ReadonlyMap<string, SpaceActivity> = new Map();
const noSessions: readonly SpaceSessionActivity[] = Object.freeze([]);
const empty: SpacesState = { loaded: false, available: false, spaces: [defaultSpace], sessions: noSessions, activity: noActivity };
/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;
/** How often the page asks what the sessions do: which ones run, failed or wait for the user. */
export const activityMilliseconds = 4000;

export type SpacesHub = ReturnType<typeof createSpacesHub>;

/**
 * Keeps what the host says of the spaces. It lists once it is connected, again each time the host says they
 * changed, and when a call that changes something returns. It also asks, every few seconds, what the sessions
 * are doing: the window marks the ones that wait for the user, and says what each space is doing once there is
 * more than the default one.
 */
export function createSpacesHub(api: SpacesApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) },
  visible: () => boolean = () => typeof document === "undefined" || document.visibilityState !== "hidden") {
  let state = empty;
  let epoch: string | null = null;
  let listing = 0;
  let latest: Promise<void> = Promise.resolve();
  let request: (kind: "show" | "changed", spaceId: string | null) => void = () => { };
  const listeners = new Set<() => void>();
  const waiters = new Set<() => void>();
  const notify = () => { for (const listener of [...listeners]) listener(); };
  const settle = () => { for (const waiter of [...waiters]) waiter(); waiters.clear(); };

  function show(next: SpacesState) {
    state = next;
    notify();
    if (next.loaded) settle();
  }

  async function list() {
    const host = epoch;
    if (!host) return;
    const turn = ++listing;
    const own = (async () => {
      try {
        const reply = await api.list({ expectedEpoch: host }, { timeoutMilliseconds: 15_000 });
        // A later listing already answered, or is about to: this one says nothing newer.
        if (turn !== listing || epoch !== host) return;
        // A host without spaces, or a catalog that cannot be read now: the window goes on with the default space, and forgets nothing.
        if (reply.status !== "ok") { show({ ...empty, loaded: true }); return; }
        const spaces = readSpaces(reply.spaces);
        const kept = sameSpaces(state.spaces, spaces) ? state.spaces : spaces;
        if (state.loaded && state.available && kept === state.spaces) return;
        // The spaces changed: what their sessions do is worked out again from the last reading.
        show({ loaded: true, available: true, spaces: kept, sessions: state.sessions,
          activity: kept.length > 1 ? spaceActivities(kept, state.sessions) : noActivity });
      } catch {
        // The host is gone: the next connection lists again. Who waited for a first answer goes on with the default space.
        if (turn === listing && epoch === host && !state.loaded) show({ ...empty, loaded: true });
      }
    })();
    latest = own;
    for (let waited = own; ; waited = latest) {
      await waited;
      if (waited === latest) return;
    }
  }

  async function readActivity() {
    const host = epoch;
    if (!host || !state.available) return;
    try {
      const reply = await api.activity({ expectedEpoch: host }, { timeoutMilliseconds: 10_000 });
      if (epoch !== host || reply.status !== "ok") return;
      const sessions = reply.sessions.map(readSessionActivity).filter(session => session !== null);
      // The sessions are known with one space too: the window marks the ones that wait for the user. What each
      // space is doing is only worked out when there is more than the default one.
      if (!sameSessions(state.sessions, sessions))
        show({ ...state, sessions, activity: state.spaces.length > 1 ? spaceActivities(state.spaces, sessions) : noActivity });
    } catch { /* What was known stays until the next reading. */ }
  }

  async function change(call: (host: string) => Promise<SpaceChangeResponse>): Promise<SpaceOutcome> {
    const host = epoch;
    if (!host) return { ok: false, status: "unavailable", message: null, space: null };
    try {
      const reply = await call(host);
      await list();
      const space = reply.space ? readSpaces([reply.space]).find(item => item.id === reply.space!.id) ?? null : null;
      return { ok: reply.status === "ok", status: reply.status, message: reply.message ?? null, space };
    } catch { return { ok: false, status: "failed", message: null, space: null }; }
  }

  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    /**
     * Waits for the first answer of the host about the spaces, at most the time given: what a window that
     * restores a space needs before it shows the projects of that space alone.
     */
    whenLoaded(milliseconds: number): Promise<void> {
      if (state.loaded) return Promise.resolve();
      return new Promise(resolve => {
        const done = () => { timers.clear(timer); waiters.delete(done); resolve(); };
        const timer = timers.set(done, milliseconds);
        waiters.add(done);
      });
    },
    /** What the page does with a request that reaches it from elsewhere: show a space, or take a change into account. */
    onRequest(handler: (kind: "show" | "changed", spaceId: string | null) => void) { request = handler; },
    /** Listens to one host until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      let clock: unknown = null;
      epoch = hostEpoch;
      const tick = () => { clock = timers.set(() => { if (visible()) void readActivity(); tick(); }, activityMilliseconds); };
      async function watch() {
        try {
          for await (const event of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            if (event.kind === "show" && typeof event.spaceId === "string") { await list(); request("show", event.spaceId); }
            else if (event.kind === "changed") { await list(); request("changed", null); }
          }
        } catch { /* Told again below. */ }
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      // A host that keeps no spaces has nothing to tell: the window then has the default space alone.
      void list().then(() => {
        if (abort.signal.aborted || !state.available) return;
        void watch();
        void readActivity();
        tick();
      });
      return () => {
        abort.abort();
        if (timer !== null) timers.clear(timer);
        if (clock !== null) timers.clear(clock);
        if (epoch === hostEpoch) { epoch = null; state = { ...empty, loaded: state.loaded }; notify(); }
      };
    },
    /** A window without a host of its own: it has the default space alone, and nobody waits for more. */
    unavailable() { if (!epoch && !state.loaded) show({ ...empty, loaded: true }); },
    /** Reads the spaces again. */
    refresh: () => list(),
    /** Reads again what the sessions of the spaces do. */
    refreshActivity: () => readActivity(),
    /** Tells the host which space the page shows: the commands of the sessions read it as their current space. */
    shown(id: string) {
      const host = epoch;
      if (host) void api.shown({ expectedEpoch: host, id }, { timeoutMilliseconds: 10_000 }).catch(() => { /* The host then goes on with what it knew. */ });
    },
    create: (name: string, description: string | null, icon: string | null, color: string | null, projectIds: readonly string[] = []) =>
      change(host => api.create({ expectedEpoch: host, name, description, icon, color, projectIds }, { timeoutMilliseconds: 30_000 })),
    /** Changes a space: a value left null stays as it is, an empty one is removed. */
    update: (id: string, edit: Readonly<{ name?: string | null; description?: string | null; icon?: string | null; color?: string | null }>) =>
      change(host => api.update({ expectedEpoch: host, id, name: edit.name ?? null, description: edit.description ?? null, icon: edit.icon ?? null, color: edit.color ?? null },
        { timeoutMilliseconds: 30_000 })),
    remove: (id: string) => change(host => api.delete({ expectedEpoch: host, id }, { timeoutMilliseconds: 30_000 })),
    /** Makes a project join spaces and leave others, in one edit. */
    assign: (projectId: string, join: readonly string[], leave: readonly string[] = []) =>
      change(host => api.assign({ expectedEpoch: host, projectId, join, leave }, { timeoutMilliseconds: 30_000 })),
    reorder: (ids: readonly string[]) => change(host => api.reorder({ expectedEpoch: host, ids }, { timeoutMilliseconds: 30_000 })),
  };
}
