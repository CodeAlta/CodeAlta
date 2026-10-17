// The page's link to the local search of the application: what the index holds and its settings, the projects and
// spaces kept out of it, and the calls that change them.
import type { localSearch, LocalSearchSettings, LocalSearchStatusResponse } from "#neoastra";

export type LocalSearchApi = Pick<typeof localSearch, "status" | "watch" | "saveSettings" | "setExcluded" | "reindex" | "turnOnMeaning" | "turnOffMeaning"
  | "pauseMeaning" | "cancelDownload" | "deleteModel" | "answerOffer">;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** What the page shows of the index. `loaded` is false until the host answered once; `available` says the host has an index. */
export type LocalSearchState = Readonly<{ loaded: boolean; available: boolean; status: LocalSearchStatusResponse | null }>;

const empty: LocalSearchState = { loaded: false, available: false, status: null };
/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;

export type LocalSearchHub = ReturnType<typeof createLocalSearchHub>;

/** Whether search is on, as the page knows it: off until the host said so. */
export const searchEnabled = (state: LocalSearchState) => state.available && state.status?.settings?.enabled === true;

/** Whether a project or a space is kept out of search. */
export function isExcluded(state: LocalSearchState, kind: "project" | "space", id: string | null | undefined): boolean {
  const settings = state.status?.settings;
  if (!settings || !id) return false;
  const ids = kind === "project" ? settings.excludedProjects : settings.excludedSpaces;
  return ids.some(other => other.toLowerCase() === id.toLowerCase());
}

/**
 * Keeps what the host says of the index: read once connected and each time the host says it changed (a turn indexed,
 * a pass done, a setting changed by the file or by an agent). A change made here is shown at once.
 */
export function createLocalSearchHub(api: LocalSearchApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let state = empty;
  let epoch: string | null = null;
  let reading = 0;
  const listeners = new Set<() => void>();
  // What the host asks the window to open (the dialog of search by meaning, from the landing card or `/search_meaning`).
  const openers = new Set<(what: "meaning") => void>();
  const publish = (next: LocalSearchState) => {
    if (JSON.stringify(next) === JSON.stringify(state)) return;
    state = next;
    for (const listener of [...listeners]) listener();
  };

  async function read() {
    const host = epoch;
    if (!host) return;
    const turn = ++reading;
    try {
      const reply = await api.status({ expectedEpoch: host }, { timeoutMilliseconds: 30_000 });
      if (epoch !== host || turn !== reading) return;
      publish(reply.status === "ok" ? { loaded: true, available: true, status: reply } : { ...empty, loaded: true });
    } catch { /* The host is gone: the next connection reads again. */ }
  }

  function shown(settings: LocalSearchSettings | null | undefined) {
    if (settings && state.status) { reading++; publish({ ...state, status: { ...state.status, settings } }); }
  }

  async function change(call: (host: string) => Promise<{ status: string; settings?: LocalSearchSettings | null }>): Promise<string> {
    const host = epoch;
    if (!host) return "unavailable";
    try {
      const reply = await call(host);
      if (epoch !== host) return "stale_epoch";
      if (reply.status === "ok") shown(reply.settings);
      void read();
      return reply.status;
    } catch { return "failed"; }
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
          for await (const event of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            void read();
            if (event.open === "meaning") for (const open of [...openers]) open("meaning");
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
    refresh: read,
    /** Listens to what the host asks the window to open, until the returned function is called. */
    onOpen(open: (what: "meaning") => void) { openers.add(open); return () => { openers.delete(open); }; },
    /** Keeps a project or a space out of search, or lets it in again. */
    setExcluded: (kind: "project" | "space", id: string, excluded: boolean) =>
      change(host => api.setExcluded({ expectedEpoch: host, kind, id, excluded }, { timeoutMilliseconds: 15_000 })),
    /** Saves the settings of search. */
    save: (settings: LocalSearchSettings) => change(host => api.saveSettings({ expectedEpoch: host, settings }, { timeoutMilliseconds: 15_000 })),
    /** Reads every source again. */
    reindex: () => change(host => api.reindex({ expectedEpoch: host, kinds: null }, { timeoutMilliseconds: 15_000 })),
    /** Turns search by meaning on with a model: it is downloaded when missing, then the index is embedded in the background. */
    turnOnMeaning: (modelId: string) => change(host => api.turnOnMeaning({ expectedEpoch: host, modelId }, { timeoutMilliseconds: 15_000 })),
    /** Turns search by meaning off; the model and its vectors stay. */
    turnOffMeaning: () => change(host => api.turnOffMeaning({ expectedEpoch: host }, { timeoutMilliseconds: 15_000 })),
    /** Pauses the indexing by meaning, or lets it go on. */
    pauseMeaning: (paused: boolean) => change(host => api.pauseMeaning({ expectedEpoch: host, paused }, { timeoutMilliseconds: 15_000 })),
    /** Stops the download of the model; the next one resumes it. */
    cancelDownload: () => change(host => api.cancelDownload({ expectedEpoch: host }, { timeoutMilliseconds: 15_000 })),
    /** Removes a model that is not in use, and its vectors. */
    deleteModel: (modelId: string) => change(host => api.deleteModel({ expectedEpoch: host, modelId }, { timeoutMilliseconds: 60_000 })),
    /** Answers the offer of search by meaning: asked again in a month, or never. */
    answerOffer: (answer: "later" | "never") => change(host => api.answerOffer({ expectedEpoch: host, answer }, { timeoutMilliseconds: 15_000 })),
  };
}
