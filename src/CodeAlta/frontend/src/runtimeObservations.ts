import type { SessionRuntimeScopedRequest, SessionRuntimeScopedResponse, WorkspaceSnapshot } from "#neoastra";
import { resolveSessionTab, tabKey, type SessionTab } from "./sessionTabs";
import { validActivity } from "./recentSessions";
import type { SessionRuntimeActivityResponse } from "#neoastra";

export type RuntimeTarget = Readonly<{ tab: SessionTab; request: SessionRuntimeScopedRequest }>;
export type RuntimeObservation = Readonly<{ label: string; details: string; stale?: boolean; epoch?: string; runtime?: string; attachment?: string; activity?: SessionRuntimeActivityResponse; running?: boolean; projectId?: string | null }>;
type Read = (request: SessionRuntimeScopedRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<SessionRuntimeScopedResponse>;
export const maximumRuntimeRows = 32;
type LiveRun = Readonly<{ running: boolean; projectId: string | null }>;
export type RuntimeObservationState = Readonly<{ rows: ReadonlyMap<string, RuntimeObservation>; summary: string; live: ReadonlyMap<string, LiveRun> }>;

/** Whether a session is working: what its open panel reports wins over the last polled observation. */
export function sessionRunning(state: RuntimeObservationState, tab: SessionTab): boolean {
  return keyRunning(state, tabKey(tab));
}
function keyRunning(state: RuntimeObservationState, key: string): boolean {
  const row = state.rows.get(key);
  return state.live.get(key)?.running ?? (!!row?.running && !row.stale);
}

/** The keys (`tabKey`) of the sessions that are working. */
export function runningSessionKeys(state: RuntimeObservationState): ReadonlySet<string> {
  const running = new Set<string>();
  for (const key of state.live.keys()) if (keyRunning(state, key)) running.add(key);
  for (const key of state.rows.keys()) if (keyRunning(state, key)) running.add(key);
  return running;
}

/** Whether any known session of a project (or global session, for null) is working. */
export function projectRunning(state: RuntimeObservationState, projectId: string | null): boolean {
  for (const run of state.live.values()) if (run.running && run.projectId === projectId) return true;
  for (const [key, row] of state.rows) if (row.projectId === projectId && row.running && !row.stale && !state.live.has(key)) return true;
  return false;
}

export function runtimeTarget(snapshot: WorkspaceSnapshot | undefined, tab: SessionTab, epoch: string | undefined): RuntimeTarget | null {
  if (!snapshot || !epoch || snapshot.sessions.filter(row => row.id === tab.sessionId).length !== 1) return null;
  const row = resolveSessionTab(snapshot, tab);
  if (!row?.createdAt || !Number.isFinite(Date.parse(row.createdAt))) return null;
  if (tab.projectId !== null && snapshot.projects.find(project => project.id === tab.projectId)?.archived) return null;
  return { tab, request: { expectedHostEpoch: epoch, sessionId: tab.sessionId, createdAt: row.createdAt,
    scope: tab.projectId === null ? "global" : "project", projectId: tab.projectId, projectPath: tab.projectId === null ? null : tab.path } };
}

export function projectRuntimeObservation(reply: SessionRuntimeScopedResponse): RuntimeObservation {
  if (reply.status !== "ok" || !reply.observation) return { label: `${reply.status === "read_failed" || reply.status === "wire_limit" ? "Error" : "Unknown"} · ${reply.status}`, details: "No runtime facts released; no command permission or completion is implied." };
  const state = reply.observation;
  if (state.status !== "ok" || state.hostEpoch !== reply.hostEpoch || state.sessionId !== reply.sessionId || !state.runtimeInstanceId)
    return { label: "Error · invalid observation", details: "Identity mismatch; no runtime facts accepted." };
  const entry = state.entry;
  if (entry && !/^[1-9][0-9]{0,18}$/u.test(entry.attachmentGeneration)) return { label: "Error · invalid attachment", details: "No runtime facts accepted." };
  const label = state.coordinatorTransitionInProgress ? "Observed transition" : !entry ? "Unknown · not attached"
    : entry.isRetiring ? "Observed retiring" : entry.isTerminated ? "Observed terminated attachment"
      : entry.activeRunId ? "Observed active run" : entry.queueDrainInProgress ? "Observed queue drain" : "Observed attached · no active run";
  return { label, epoch: reply.hostEpoch, runtime: state.runtimeInstanceId, attachment: entry?.attachmentGeneration,
    running: !!entry?.activeRunId && !entry.isRetiring && !entry.isTerminated,
    activity: validActivity(entry?.activity) ? entry.activity : undefined,
    details: `${label}. Observed ${new Date().toISOString()}; may already be stale, not liveness/finality or command authority. Runtime ${state.runtimeInstanceId}; attachment ${entry?.attachmentGeneration ?? "absent"}; run ${entry?.activeRunId ?? "absent"}; transition ${state.coordinatorTransitionInProgress}; retiring ${entry?.isRetiring ?? "unknown"}; terminated ${entry?.isTerminated ?? "unknown"}; queue drain ${entry?.queueDrainInProgress ?? "unknown"}.` };
}

export function createRuntimeObservations(read: Read) {
  let generation = 0;
  let abort: AbortController | undefined;
  let fences = new Map<string, RuntimeObservation>();
  let state: RuntimeObservationState = { rows: new Map(), summary: "Not observed. Refresh explicitly; no polling.", live: new Map() };
  const listeners = new Set<() => void>();
  // The sessions that work: the same set is given again while it holds the same sessions, so what follows it
  // is not rendered again by the answers of a refresh that change nothing of it.
  let running: ReadonlySet<string> = new Set();
  const publish = (rows: ReadonlyMap<string, RuntimeObservation>, summary: string, live = state.live) => {
    state = { rows, summary, live };
    const next = runningSessionKeys(state);
    if (next.size !== running.size || [...next].some(key => !running.has(key))) running = next;
    for (const listener of listeners) listener();
  };
  const cancel = () => { generation++; abort?.abort(); abort = undefined; };
  function invalidate() {
    cancel();
    if (state.rows.size) publish(new Map([...state.rows].map(([key, row]) => [key, { ...row,
      label: row.label.startsWith("Loading") ? "Unknown · canceled/omitted" : row.label, stale: true }])), "Stale observations — scope changed. Refresh explicitly.");
  }
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    /** The keys (`tabKey`) of the sessions that work; the set changes only when one starts or stops. */
    getRunning: () => running,
    invalidate,
    /** Records what the open panel of a session sees of its run; null when the panel stops watching. */
    setLive(tab: SessionTab, running: boolean | null) {
      const key = tabKey(tab), current = state.live.get(key);
      if (running === null ? !current : current?.running === running) return;
      const live = new Map(state.live);
      if (running === null) live.delete(key); else live.set(key, { running, projectId: tab.projectId });
      publish(state.rows, state.summary, live);
    },
    async refresh(targets: readonly RuntimeTarget[], omitted = 0) {
      const before = state.rows;
      // The rows are replaced at once by the ones being read, which keep what was known: nothing is shown stale between.
      cancel(); const version = generation; const controller = new AbortController(); abort = controller;
      const unique = new Map(targets.map(target => [tabKey(target.tab), target]));
      const selected = [...unique.values()].slice(0, maximumRuntimeRows);
      fences = new Map(selected.flatMap(target => { const key = tabKey(target.tab); const prior = fences.get(key); return prior ? [[key, prior] as const] : []; }));
      const missing = omitted + targets.length - selected.length;
      // A row keeps its last known activity while it reloads, so a running indicator does not blink on every refresh.
      const rows = new Map<string, RuntimeObservation>(selected.map(target => { const key = tabKey(target.tab), prior = before.get(key);
        return [key, { label: "Loading observation…", details: "Explicit read only; no permission implied.", running: prior?.running, projectId: prior?.projectId }]; }));
      const summary = (count: number) => `${count}/${selected.length} observed responses; ${missing} omitted/unverified (maximum 32 per refresh). Not atomic across actors; never a recentness or liveness report.`;
      publish(rows, summary(0));
      let completed = 0;
      // Sequential bounded unary reads; at most 32 × 64 KiB responses (2 MiB), no hidden attachments.
      // The 20-second batch budget is a canceled wait, not an actor-work cancellation guarantee.
      const deadline = setTimeout(() => controller.abort(), 20_000);
      try {
        for (const target of selected) {
          if (version !== generation) return;
          const key = tabKey(target.tab);
          let observed: RuntimeObservation;
          try {
            if (controller.signal.aborted) throw new Error("batch canceled");
            const reply = await read(target.request, { signal: controller.signal, timeoutMilliseconds: 10_000 });
            if (version !== generation || controller.signal.aborted) return;
            const request = target.request;
            observed = reply.hostEpoch === request.expectedHostEpoch && reply.sessionId === request.sessionId && reply.scope === request.scope
              && reply.projectId === request.projectId && reply.projectPath === request.projectPath ? projectRuntimeObservation(reply)
                : { label: "Error · identity mismatch", details: "No runtime facts accepted." };
            const prior = fences.get(key);
            if (observed.runtime && prior && prior.epoch === observed.epoch && prior.runtime && prior.runtime !== observed.runtime)
              observed = { label: "Error · runtime identity changed", details: "Same-host runtime identity mismatch; no facts accepted." };
            if (observed.runtime && prior && prior.epoch === observed.epoch && prior.runtime === observed.runtime && observed.attachment && prior.attachment
              && BigInt(observed.attachment) < BigInt(prior.attachment)) observed = { label: "Stale attachment", details: "Older attachment generation refused." };
            if (observed.runtime) fences.set(key, { ...observed, attachment: observed.attachment ?? (prior && prior.epoch === observed.epoch ? prior.attachment : undefined) });
          } catch { observed = { label: controller.signal.aborted ? "Unknown · canceled/omitted" : "Error · read failed", details: "No runtime facts accepted; refresh is explicit." }; }
          if (version !== generation) return;
          rows.set(key, { ...observed, projectId: target.tab.projectId }); completed++; publish(new Map(rows), summary(completed));
        }
      } finally {
        clearTimeout(deadline);
        if (version === generation && controller.signal.aborted) {
          for (const [key, row] of rows) if (row.label.startsWith("Loading")) rows.set(key, { label: "Unknown · canceled/omitted", details: "Batch time budget expired." });
          publish(new Map(rows), summary(completed));
        }
      }
    },
  };
}
