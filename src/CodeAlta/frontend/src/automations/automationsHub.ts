// The page's link to the automations of the application: what they are now, and the calls that change them.
import type { automations, AutomationInput, AutomationItem, AutomationRunItem, AutomationsListResponse, AutomationTriggerItem } from "#neoastra";

export type AutomationsApi = Pick<typeof automations, "list" | "refresh" | "save" | "delete" | "setEnabled" | "allow" | "setPaused" | "run" | "runs" | "preview" | "watch">;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** What the page shows of the automations. `loaded` is false until the host answered once. */
export type AutomationsState = Readonly<{
  loaded: boolean; available: boolean; paused: boolean; scanned: boolean;
  items: readonly AutomationItem[]; faults: AutomationsListResponse["faults"]; runs: readonly AutomationRunItem[]; upcoming: AutomationsListResponse["upcoming"];
}>;
/** How a change ended: done, or refused with what the host said. */
export type AutomationOutcome = Readonly<{ ok: boolean; id?: string | null; message?: string | null; run?: AutomationRunItem | null }>;

const empty: AutomationsState = { loaded: false, available: false, paused: false, scanned: false, items: [], faults: [], runs: [], upcoming: [] };
/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;
/** How often the times shown ("in 5 min.") are brought up to date while nothing else changes. */
export const refreshMilliseconds = 60_000;

export type AutomationsHub = ReturnType<typeof createAutomationsHub>;

/**
 * Keeps what the host says of the automations. It lists once it is connected and again each time the host
 * says something changed; the calls that change something list again when they return.
 */
export function createAutomationsHub(api: AutomationsApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let state = empty;
  let epoch: string | null = null;
  let listing = 0;
  let latest: Promise<void> = Promise.resolve();
  const listeners = new Set<() => void>();
  const notify = () => { for (const listener of [...listeners]) listener(); };

  function take(reply: AutomationsListResponse, host: string) {
    if (epoch !== host) return;
    state = reply.status === "ok"
      ? { loaded: true, available: true, paused: reply.paused, scanned: reply.scanned, items: reply.items, faults: reply.faults, runs: reply.runs, upcoming: reply.upcoming }
      : { ...empty, loaded: true };
    notify();
  }

  async function list(read: "list" | "refresh" = "list") {
    const host = epoch;
    if (!host) return;
    const turn = ++listing;
    const own = (async () => {
      try {
        const reply = await api[read]({ expectedEpoch: host }, { timeoutMilliseconds: 30_000 });
        // A later listing already answered, or is about to: this one says nothing newer.
        if (turn === listing) take(reply, host);
      } catch { /* The host is gone: the next connection lists again. */ }
    })();
    latest = own;
    // Who waits for a listing reads the list next: it waits for the newest one, which the host may have asked for meanwhile.
    for (let waited = own; ; waited = latest) {
      await waited;
      if (waited === latest) return;
    }
  }

  async function change(call: (host: string) => Promise<Readonly<{ status: string; id?: string | null; message?: string | null }>>): Promise<AutomationOutcome> {
    const host = epoch;
    if (!host) return { ok: false, message: null };
    try {
      const reply = await call(host);
      await list();
      return { ok: reply.status === "ok", id: reply.id ?? null, message: reply.message ?? null };
    } catch { return { ok: false, message: null }; }
  }

  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    /** Listens to one host until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      let clock: unknown = null;
      epoch = hostEpoch;
      state = empty;
      notify();
      const tick = () => { clock = timers.set(() => { void list(); tick(); }, refreshMilliseconds); };
      async function watch() {
        try {
          for await (const _ of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            void list();
          }
        } catch { /* Told again below. */ }
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void watch();
      tick();
      return () => {
        abort.abort();
        if (timer !== null) timers.clear(timer);
        if (clock !== null) timers.clear(clock);
        if (epoch === hostEpoch) { epoch = null; state = empty; notify(); }
      };
    },
    /** Reads the configuration files again. */
    refresh: () => list("refresh"),
    save: (automation: AutomationInput, storeProjectId: string | null) =>
      change(host => api.save({ expectedEpoch: host, automation, storeProjectId }, { timeoutMilliseconds: 30_000 })),
    remove: (id: string) => change(host => api.delete({ expectedEpoch: host, id }, { timeoutMilliseconds: 30_000 })),
    setEnabled: (id: string, enabled: boolean) => change(host => api.setEnabled({ expectedEpoch: host, id, enabled }, { timeoutMilliseconds: 30_000 })),
    /** Allows the triggers of an automation that came with its project to start it, as it is now. */
    allow: (id: string) => change(host => api.allow({ expectedEpoch: host, id }, { timeoutMilliseconds: 15_000 })),
    setPaused: (paused: boolean) => change(host => api.setPaused({ expectedEpoch: host, paused }, { timeoutMilliseconds: 15_000 })),
    /** Runs an automation now. The answer comes once its session exists. */
    async run(id: string): Promise<AutomationOutcome> {
      const host = epoch;
      if (!host) return { ok: false };
      try {
        const reply = await api.run({ expectedEpoch: host, id }, { timeoutMilliseconds: 120_000 });
        await list();
        const run = reply.run ?? null;
        return { ok: reply.status === "ok" && run !== null && run.status !== "failed", run, message: run?.message ?? null };
      } catch { return { ok: false }; }
    },
    /** The runs of one automation, newest first. */
    async runs(id: string): Promise<readonly AutomationRunItem[]> {
      const host = epoch;
      if (!host) return [];
      try {
        const reply = await api.runs({ expectedEpoch: host, id, limit: 100 }, { timeoutMilliseconds: 15_000 });
        return reply.status === "ok" ? reply.runs : [];
      } catch { return []; }
    },
    /** The next times a trigger being written is due, or what is wrong with it. */
    async preview(trigger: AutomationTriggerItem, signal: AbortSignal): Promise<Readonly<{ times: readonly string[]; problem: string | null }> | null> {
      const host = epoch;
      if (!host) return null;
      try {
        const reply = await api.preview({ expectedEpoch: host, trigger }, { signal, timeoutMilliseconds: 10_000 });
        return reply.status === "ok" ? { times: reply.times, problem: null } : reply.status === "refused" ? { times: [], problem: reply.message ?? null } : null;
      } catch { return null; }
    },
  };
}
