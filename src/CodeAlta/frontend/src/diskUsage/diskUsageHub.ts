import type { DiskUsageCheckout, DiskUsageCleanResponse, DiskUsageLastResponse, DiskUsageProgress } from "#neoastra";
import { storageBytes } from "../storage/storage";
import { measurementView, withCheckout, withProject, type DiskUsageApi, type DiskUsageView } from "./diskUsage";

/** The measurement shown, and whether it was made in this run of the application. */
export type DiskUsageShown = Readonly<{ view: DiskUsageView; current: boolean }>;
/** A measurement that runs: the records that arrived, and how far it is. */
export type DiskUsageScan = Readonly<{ view: DiskUsageView; progress: DiskUsageProgress | null }>;
/** How the last measurement ended, when it did not give one. */
export type DiskUsageEnding = Readonly<{ kind: "stopped" }> | Readonly<{ kind: "failed"; problem: string | null }> | Readonly<{ kind: "refused"; status: string }>;
/** What changed since the measurement: the projects whose worktrees were removed, and those worktrees. */
export type DiskUsageStale = Readonly<{ projects: ReadonlySet<string>; paths: ReadonlySet<string> }>;

/** What every view of the disk usage shows. */
export type DiskUsageState = Readonly<{ loading: boolean; shown: DiskUsageShown | null; scan: DiskUsageScan | null; ending: DiskUsageEnding | null; stale: DiskUsageStale }>;

/**
 * The disk usage of a window, for every view that shows it (the tab and the Storage page): one state, one measurement
 * followed at a time. A view attaches while it is shown; the measurement stops when the last view went, a moment
 * later, so that a view mounted again keeps it.
 * @param api The service of the host.
 * @param later Runs the release of the last view after the current task: a test runs it itself.
 */
export function createDiskUsageHub(api: DiskUsageApi, later: (run: () => void) => void = run => { setTimeout(run, 0); }) {
  const noStale: DiskUsageStale = { projects: new Set<string>(), paths: new Set<string>() };
  const initial: DiskUsageState = { loading: true, shown: null, scan: null, ending: null, stale: noStale };
  let state = initial;
  let epoch: string | null | undefined;
  let views = 0;
  let following: AbortController | null = null;
  const listeners = new Set<() => void>();
  function set(change: Partial<DiskUsageState>) {
    state = { ...state, ...change };
    for (const listener of [...listeners]) listener();
  }

  async function readLast(): Promise<DiskUsageLastResponse | null> {
    const asked = epoch;
    if (!asked) { set({ loading: false, ending: { kind: "refused", status: "unavailable" } }); return null; }
    const reply = await api.last({ expectedEpoch: asked }, { timeoutMilliseconds: 30_000 }).catch(() => null);
    // An answer to another host says nothing of this one.
    if (asked !== epoch) return null;
    if (!reply) { set({ loading: false, ending: { kind: "failed", problem: null } }); return null; }
    if (reply.status !== "ok") { set({ loading: false, ending: { kind: "refused", status: reply.status } }); return null; }
    set({ loading: false, shown: reply.measurement ? { view: measurementView(reply.measurement), current: reply.current } : null });
    return reply;
  }

  // Follows a measurement: the one that runs, or a new one. One at a time: asked again, it is the same.
  async function measure() {
    const asked = epoch;
    if (!asked || following) return;
    const controller = new AbortController();
    following = controller;
    let view: DiskUsageView = { projects: [], done: null };
    set({ ending: null, scan: { view, progress: null } });
    let ended: DiskUsageEnding | null = { kind: "failed", problem: null };
    try {
      const stream = await api.scan({ expectedEpoch: asked }, { signal: controller.signal, timeoutMilliseconds: 30_000 });
      for await (const event of stream) {
        if (event.status !== "ok") { ended = { kind: "refused", status: event.status }; break; }
        if (event.kind === "progress" && event.progress) { set({ scan: { view, progress: event.progress } }); continue; }
        if (event.kind === "project" && event.project) view = withProject(view, event.project);
        else if (event.kind === "done" && event.done) {
          ended = null;
          set({ shown: { view: { ...view, done: event.done }, current: true }, stale: noStale });
          break;
        } else if (event.kind === "failed") { ended = { kind: "failed", problem: event.problem }; break; }
        set({ scan: { view, progress: state.scan?.progress ?? null } });
      }
    } catch { /* Said below: stopped, or failed. */ }
    finally {
      if (following === controller) following = null;
    }
    // A stream that ends because it was stopped ends without a record that says so.
    if (ended && controller.signal.aborted) ended = { kind: "stopped" };
    if (asked !== epoch) return;
    set({ scan: null, ending: ended });
    // The measurement as the host keeps it, in the order of the catalog.
    if (!ended) void readLast();
  }

  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    /**
     * A view is shown: the last measurement is read, and a measurement the host runs is followed.
     * @returns What the view calls when it goes.
     */
    attach(nextEpoch: string | null): () => void {
      views++;
      if (nextEpoch !== epoch) {
        following?.abort();
        following = null;
        epoch = nextEpoch;
        state = { ...initial };
      }
      if (!following) void readLast().then(reply => { if (reply?.running) void measure(); });
      let attached = true;
      return () => {
        if (!attached) return;
        attached = false;
        views--;
        later(() => { if (views === 0) following?.abort(); });
      };
    },
    /** Measures again, or joins the measurement that runs. */
    measure() { void measure(); },
    /** Stops following the measurement: the host stops it when no other window follows it. */
    stop() { following?.abort(); },
    /**
     * Cleans the build output of checkouts, one after the other: each is shown measured again as soon as its answer
     * arrives, and once the clean ends, however it ends, the totals of the host are read again. The clean belongs to the
     * hub: the view that asked may go meanwhile, and every view sees the end.
     * @param progress Told where the clean is; a view that went just stops listening.
     * @param stopped Asked before each checkout: true leaves the ones that remain.
     */
    async clean(items: readonly DiskUsageCleanItem[], { progress, stopped }: Readonly<{ progress?: (at: DiskUsageCleanProgress) => void; stopped?: () => boolean }> = {})
      : Promise<readonly DiskUsageCleanAnswer[]> {
      const asked = epoch;
      const answers: DiskUsageCleanAnswer[] = [];
      try {
        for (const [index, item] of items.entries()) {
          if (!asked || stopped?.()) break;
          const freed = answers.reduce((sum, answer) => sum + storageBytes(answer.result.bytesFreed), 0);
          progress?.({ index, done: 0, total: item.paths.length, freed });
          let result: DiskUsageCleanResponse | null = null;
          try {
            const stream = await api.clean({ expectedEpoch: asked, projectId: item.projectId, checkout: item.checkout.path, measureId: item.checkout.measureId, paths: [...item.paths] },
              { timeoutMilliseconds: 30_000 });
            for await (const event of stream) {
              if (event.kind === "running" && event.progress) progress?.({ index, done: event.progress.done, total: event.progress.total, freed });
              if (event.kind === "done" && event.result) result = event.result;
            }
          } catch { /* The answer did not arrive: what the clean did is not known. */ }
          answers.push({ item, result: result ?? { status: "unconfirmed", message: null, entries: [], bytesFreed: "0", moreRemain: false, checkout: null } });
          const fresh = result?.checkout;
          if (fresh && asked === epoch && state.shown) set({ shown: { ...state.shown, view: withCheckout(state.shown.view, item.projectId, fresh) } });
        }
      } finally {
        // The totals of the host follow the clean.
        if (asked === epoch) void readLast();
      }
      return answers;
    },
    /** Worktrees of a project were removed since the measurement. */
    removed(projectId: string, paths: readonly string[]) {
      if (paths.length === 0) return;
      set({ stale: { projects: new Set([...state.stale.projects, projectId]), paths: new Set([...state.stale.paths, ...paths]) } });
    },
  };
}

export type DiskUsageHub = ReturnType<typeof createDiskUsageHub>;

/** A checkout to clean: the project that carries it, the checkout as the view showed it, and the folders the user confirmed. */
export type DiskUsageCleanItem = Readonly<{ projectId: string; checkout: DiskUsageCheckout; paths: readonly string[] }>;
/** Where a clean is: the checkout it cleans (counted from 0), the folders of it done, and what the checkouts before it freed. */
export type DiskUsageCleanProgress = Readonly<{ index: number; done: number; total: number; freed: number }>;
/** What a clean said of one checkout; `unconfirmed` when no answer arrived. */
export type DiskUsageCleanAnswer = Readonly<{ item: DiskUsageCleanItem; result: DiskUsageCleanResponse }>;
