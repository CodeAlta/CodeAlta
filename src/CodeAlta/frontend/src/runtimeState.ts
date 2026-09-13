import type { SessionRuntimeStateRequest, SessionRuntimeStateResponse } from "#neoastra";

export type RuntimeState =
  | { kind: "loading" }
  | { kind: "ready"; snapshot: SessionRuntimeStateResponse }
  | { kind: "error"; code: string };

// App-owned identity/reload latch, one original frontend waiter and one latest explicit pending refresh.
// A generated rejection/timeout settles only that frontend waiter, not underlying host work.
export function createRuntimeStateReader(
  invoke: (request: SessionRuntimeStateRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<SessionRuntimeStateResponse>,
) {
  let runtimeInstanceId: string | null = null;
  let reloadCode: string | null = null;
  type Selection = { request: Readonly<SessionRuntimeStateRequest>; signal: AbortSignal; publish: (state: RuntimeState) => void;
    observeIdentity?: (result: { status: string; epoch: string | null }) => void; revision: number; removeAbort: () => void };
  type Outcome = "settled" | "superseded" | "detached" | "blocked";
  type Refresh = { selection: Selection; revision: number; resolve: (outcome: Outcome) => void };
  type Owner = { refresh: Refresh; controller: AbortController; waiter?: Promise<SessionRuntimeStateResponse>; failure?: unknown };
  let selected: Selection | null = null;
  let pending: Refresh | null = null;
  let active: Owner | null = null;
  let work = Promise.resolve();
  let notificationFailure: unknown;
  const current = (selection: Selection) => selected === selection && !selection.signal.aborted;
  function publish(selection: Selection, state: RuntimeState) {
    if (!current(selection)) return;
    try { selection.publish(state); } catch (error) { notificationFailure ??= error; }
  }
  function discardPending(outcome: Outcome) {
    const displaced = pending; pending = null;
    displaced?.resolve(outcome); // No accumulated promises waiting on the original RPC.
  }
  function invalidate(selection: Selection, code: string) {
    reloadCode ??= code; // Commit independently of any presentation/subscriber callback.
    discardPending("blocked");
    try { selection.observeIdentity?.({ status: reloadCode, epoch: null }); }
    catch (error) { notificationFailure ??= error; }
    if (selected) publish(selected, { kind: "error", code: reloadCode });
  }
  async function read(owner: Owner) {
    const { selection, revision } = owner.refresh;
    const error = (code: string) => {
      if (selection.revision === revision) publish(selection, { kind: "error", code });
    };
    try {
      owner.waiter = invoke(selection.request, { signal: owner.controller.signal, timeoutMilliseconds: 8_000 });
      const snapshot = await owner.waiter;
      // Validate correlation before considering host/runtime evidence, and evidence before presentation fences.
      if (!correlatedIdentity(snapshot, selection.request.sessionId)) { error("invalid_response"); return; }
      if (snapshot.status === "stale_epoch" || snapshot.hostEpoch !== selection.request.expectedHostEpoch) {
        invalidate(selection, "stale_epoch"); return;
      }
      if (snapshot.status === "ok") {
        if (runtimeInstanceId !== null && runtimeInstanceId !== snapshot.runtimeInstanceId) {
          invalidate(selection, "stale_runtime"); return;
        }
        runtimeInstanceId = snapshot.runtimeInstanceId;
      }
      if (!current(selection) || selection.revision !== revision || reloadCode) return;
      if (snapshot.status !== "ok") { error(snapshot.status); return; }
      publish(selection, { kind: "ready", snapshot });
    } catch (failure) {
      owner.failure = failure;
      error("read_failed"); // Sanitized frontend observation failure; no retry or backend-termination inference.
    } finally {
      // A refresh outcome is not the worker/drain join exposed by settled().
      owner.refresh.resolve(reloadCode ? "blocked" : !current(selection) ? "detached" : selection.revision !== revision ? "superseded" : "settled");
    }
  }
  function start() {
    if (active || !pending || reloadCode) return;
    const refresh = pending; pending = null;
    active = { refresh, controller: new AbortController() };
    // Retain the original drain before its scheduled launch. It owns each original read through
    // completion, including a successor explicitly queued while the preceding read was held.
    // There is no chain of per-refresh workers or retained history of completed drains.
    work = Promise.resolve().then(async () => {
      while (active) {
        await read(active);
        active = null;
        if (!pending || reloadCode) return;
        const next = pending; pending = null;
        active = { refresh: next, controller: new AbortController() };
      }
    });
  }
  return {
    notificationFailure: () => notificationFailure,
    // Joins this drain's original worker plus already-explicit pending work, including late
    // admissions during that drain. A later refresh after the worker becomes idle creates a new drain.
    // This joins frontend processing only: no generated settlement proves host termination.
    settled: () => work,
    forSelection(request: SessionRuntimeStateRequest, signal: AbortSignal, notify: (state: RuntimeState) => void,
      observeIdentity?: Selection["observeIdentity"]) {
      selected?.removeAbort();
      discardPending("detached");
      active?.controller.abort();
      const selection: Selection = { request: Object.freeze({ ...request }), signal, publish: notify, observeIdentity, revision: 0, removeAbort: () => {} };
      selected = selection;
      const detach = () => {
        if (pending?.selection === selection) discardPending("detached");
        if (active?.refresh.selection === selection) active.controller.abort();
        selection.removeAbort();
      };
      selection.removeAbort = () => signal.removeEventListener("abort", detach);
      if (!signal.aborted) signal.addEventListener("abort", detach, { once: true });
      if (reloadCode) publish(selection, { kind: "error", code: reloadCode });
      return {
        refresh(): Promise<Outcome> {
          if (!current(selection)) return Promise.resolve("detached");
          if (reloadCode) { publish(selection, { kind: "error", code: reloadCode }); return Promise.resolve("blocked"); }
          discardPending("superseded");
          let resolve!: Refresh["resolve"];
          const promise = new Promise<Outcome>(done => { resolve = done; });
          pending = { selection, revision: ++selection.revision, resolve };
          // Reserve the original owner and retain its drain before notifying reentrant subscribers.
          // Invocation remains scheduled, so loading still precedes the original RPC call.
          start();
          publish(selection, { kind: "loading" });
          return promise;
        },
      };
    },
  };
}

function identity(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 256 ||
    /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]$/.test(value)) return false;
  for (let index = 0; index < value.length; index++) {
    const unit = value.charCodeAt(index);
    if (unit < 0xd800 || unit > 0xdfff) continue;
    if (unit > 0xdbff || ++index === value.length) return false;
    const low = value.charCodeAt(index);
    if (low < 0xdc00 || low > 0xdfff) return false;
  }
  return true;
}

function correlatedIdentity(snapshot: SessionRuntimeStateResponse, sessionId: string): boolean {
  if (!snapshot || !["ok", "invalid_request", "stale_epoch", "wire_limit", "closed", "read_failed"].includes(snapshot.status) ||
    !identity(snapshot.hostEpoch) || !identity(snapshot.sessionId) || snapshot.sessionId !== sessionId) return false;
  if ((snapshot.runtimeInstanceId !== null && !identity(snapshot.runtimeInstanceId)) ||
    (snapshot.coordinatorTransitionInProgress !== null && typeof snapshot.coordinatorTransitionInProgress !== "boolean") ||
    (snapshot.entry !== null && (!identity(snapshot.entry?.attachmentGeneration) || !identity(snapshot.entry.providerId) ||
      !identity(snapshot.entry.providerKey) || (snapshot.entry.activeRunId !== null && !identity(snapshot.entry.activeRunId))))) return false;
  return snapshot.status !== "ok" || (identity(snapshot.runtimeInstanceId) && typeof snapshot.coordinatorTransitionInProgress === "boolean");
}
