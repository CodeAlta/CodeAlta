// The page's link to the work items of the application: what they are now, and the calls that change them.
import type { workItems, WorkItemActionResponse, WorkItemsProject, WorkItemsSettings } from "#neoastra";
import { defaultWorkSettings, type WorkKind } from "./workItems";

export type WorkItemsApi = Pick<typeof workItems, "list" | "read" | "act" | "saveSettings" | "watch">;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/**
 * What the page shows of the work items. `loaded` is false until the host answered once; `complete` until
 * it answered for every project: the projects used last are read first, and shown as they come.
 */
export type WorkItemsState = Readonly<{ loaded: boolean; complete: boolean; available: boolean; projects: readonly WorkItemsProject[]; settings: WorkItemsSettings }>;
/** How a change ended: done, or refused with what the host said. */
export type WorkItemOutcome = Readonly<{ ok: boolean; status: string; message: string | null; reason: string | null; sessionId: string | null;
  prompt: string | null; agentPromptId: string | null }>;
export type WorkItemTarget = Readonly<{ projectId: string; kind: string; id: string }>;

const empty: WorkItemsState = { loaded: false, complete: false, available: false, projects: [], settings: defaultWorkSettings };
const failed: WorkItemOutcome = { ok: false, status: "failed", message: null, reason: null, sessionId: null, prompt: null, agentPromptId: null };
/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;
/** How often the files are read again while nothing tells of a change: agents and people write them directly. */
export const refreshMilliseconds = 30_000;
/** How many projects each request of a reading names: few at first, so that the first answer comes fast. */
export const readingSteps: readonly number[] = [3, 6, 12, 24];

/** The projects of a reading, cut in the requests that read them. */
export function readingBatches(projects: readonly string[]): readonly (readonly string[])[] {
  const batches: string[][] = [];
  for (let start = 0, step = 0; start < projects.length; step++) {
    const size = readingSteps[Math.min(step, readingSteps.length - 1)];
    batches.push(projects.slice(start, start + size));
    start += size;
  }
  return batches;
}

export type WorkItemsHub = ReturnType<typeof createWorkItemsHub>;

/**
 * Keeps what the host says of the tasks and the plans. It reads them once it is connected and knows the
 * projects, each time the host says something changed, and now and then: a plan is a file an agent writes
 * without telling anyone. A reading goes through the projects a few at a time, in the order the window gives
 * (the ones used last first), and what it found so far is shown at once: nothing waits for the last project.
 */
export function createWorkItemsHub(api: WorkItemsApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let state = empty;
  let epoch: string | null = null;
  let order: readonly string[] | null = null;
  let reading = 0;
  let latest: Promise<void> = Promise.resolve();
  const listeners = new Set<() => void>();
  const publish = (next: WorkItemsState) => {
    // The same answer again changes nothing on the page.
    if (JSON.stringify(next) === JSON.stringify(state)) return;
    state = next;
    for (const listener of [...listeners]) listener();
  };

  // Puts what the host said of some projects in the place of what was known of them, in the order of the window.
  function merge(asked: readonly string[], found: readonly WorkItemsProject[]): readonly WorkItemsProject[] {
    const known = new Map(state.projects.map(project => [project.projectId, project]));
    for (const id of asked) known.delete(id);
    for (const project of found) known.set(project.projectId, project);
    const places = new Map((order ?? []).map((id, index) => [id, index]));
    return [...known.values()].filter(project => places.has(project.projectId)).sort((a, b) => places.get(a.projectId)! - places.get(b.projectId)!);
  }

  async function ask(host: string, projects: readonly string[], current: () => boolean): Promise<boolean> {
    const reply = await api.list({ expectedEpoch: host, projectIds: [...projects] }, { timeoutMilliseconds: 30_000 });
    if (epoch !== host || !current()) return false;
    if (reply.status !== "ok") { publish({ ...empty, loaded: true, complete: true }); return false; }
    publish({ ...state, loaded: true, available: true, projects: merge(projects, reply.projects), settings: reply.settings ?? state.settings });
    return true;
  }

  async function read() {
    const host = epoch;
    const projects = order;
    if (!host || !projects) return;
    const turn = ++reading;
    const own = (async () => {
      try {
        // Without a project there is still the answer that says whether the host keeps work items, and the settings.
        for (const batch of projects.length ? readingBatches(projects) : [[]]) if (!await ask(host, batch, () => turn === reading)) return;
        publish({ ...state, complete: true, projects: merge([], []) });
      } catch { /* The host is gone: the next connection reads again. */ }
    })();
    latest = own;
    // Who waits for a reading reads the list next: it waits for the newest one, which the host may have asked for meanwhile.
    for (let waited = own; ; waited = latest) {
      await waited;
      if (waited === latest) return;
    }
  }

  // After a change: its project is read at once, the others at the next reading.
  async function readProject(projectId: string) {
    const host = epoch;
    if (!host) return;
    try { await ask(host, [projectId], () => true); } catch { /* Read again at the next reading. */ }
  }

  async function act(target: WorkItemTarget, action: string, more: Readonly<{ value?: string; sessionId?: string | null; workingDirectory?: string | null }> = {}): Promise<WorkItemOutcome> {
    const host = epoch;
    if (!host) return failed;
    try {
      const reply: WorkItemActionResponse = await api.act({ expectedEpoch: host, projectId: target.projectId, kind: target.kind, id: target.id, action,
        value: more.value ?? null, sessionId: more.sessionId ?? null, workingDirectory: more.workingDirectory ?? null }, { timeoutMilliseconds: 600_000 });
      await readProject(target.projectId);
      return { ok: reply.status === "ok", status: reply.status, message: reply.message ?? null, reason: reply.reason ?? null, sessionId: reply.sessionId ?? null,
        prompt: reply.prompt ?? null, agentPromptId: reply.agentPromptId ?? null };
    } catch { return failed; }
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
      publish(empty);
      const tick = () => { clock = timers.set(() => { void read(); tick(); }, refreshMilliseconds); };
      async function watch() {
        try {
          for await (const _ of await api.watch({ expectedEpoch: hostEpoch, projectIds: null }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            void read();
          }
        } catch { /* Told again below. */ }
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void watch();
      tick();
      // The projects are known from an earlier host: this one is read at once.
      if (order) void read();
      return () => {
        abort.abort();
        if (timer !== null) timers.clear(timer);
        if (clock !== null) timers.clear(clock);
        if (epoch === hostEpoch) { epoch = null; reading++; publish(empty); }
      };
    },
    /**
     * Says which projects the window has, the ones used last first. The first time starts the first reading;
     * a new order, or a new project, is read at the next one.
     */
    setProjects(projects: readonly string[]) {
      const first = order === null;
      if (!first && order!.length === projects.length && order!.every((id, index) => id === projects[index])) return;
      const added = !first && projects.some(id => !order!.includes(id));
      order = [...projects];
      if (first || added) void read();
    },
    /** Reads the files again: a run ended, a tab came to the front. */
    refresh: read,
    /** The text of one item, or null when it cannot be read. */
    async read(target: WorkItemTarget, signal?: AbortSignal): Promise<Readonly<{ markdown: string; truncated: boolean }> | null> {
      const host = epoch;
      if (!host) return null;
      try {
        const reply = await api.read({ expectedEpoch: host, projectId: target.projectId, kind: target.kind as WorkKind, id: target.id }, { signal, timeoutMilliseconds: 15_000 });
        return reply.status === "ok" && reply.markdown !== null ? { markdown: reply.markdown, truncated: reply.truncated } : null;
      } catch { return null; }
    },
    act,
    async saveSettings(settings: WorkItemsSettings): Promise<boolean> {
      const host = epoch;
      if (!host) return false;
      try {
        const reply = await api.saveSettings({ expectedEpoch: host, settings }, { timeoutMilliseconds: 15_000 });
        if (reply.status === "ok" && reply.settings) publish({ ...state, settings: reply.settings });
        return reply.status === "ok";
      } catch { return false; }
    },
  };
}
