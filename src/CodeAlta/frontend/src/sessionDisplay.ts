import type { SessionDisplayItem, SessionDisplayRequest, SessionDisplayText, SessionDisplayToolActivity } from "#neoastra";

type OpenDisplay = (request: SessionDisplayRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<AsyncIterable<SessionDisplayItem>>;
export type DisplayState = Readonly<{
  kind: "idle" | "loading" | "connected" | "closed" | "error";
  hostEpoch: string | null;
  sessionId: string | null;
  snapshot: SessionDisplayItem | null;
  code: string | null;
  cleanupBlocked: boolean;
}>;

// Instance-owned, selected-session-only renderer state. NeoAstra owns the actual channel/credits/buffering.
export function createSessionDisplayStore(open: OpenDisplay) {
  let state: DisplayState = Object.freeze({ kind: "idle", hostEpoch: null, sessionId: null, snapshot: null, code: null, cleanupBlocked: false });
  const listeners = new Set<() => void>();
  type Selection = { hostEpoch: string; sessionId: string; controller: AbortController;
    observeIdentity?: (result: { status: string; epoch: string | null }) => void };
  type Owner = { selection: Selection; opening?: Promise<AsyncIterable<SessionDisplayItem>>; stream?: AsyncIterable<SessionDisplayItem>;
    iterator?: AsyncIterator<SessionDisplayItem>; next?: Promise<IteratorResult<SessionDisplayItem>>;
    returning?: Promise<IteratorResult<SessionDisplayItem>>; failure?: unknown };
  let selected: Selection | null = null;
  let desired: Selection | null = null;
  let active: Owner | null = null;
  let blocked: { owner: Owner; error: unknown } | null = null;
  let reload = false;
  let notificationFailure: unknown;
  let running = false;
  let work = Promise.resolve();
  function publish(value: Omit<DisplayState, "cleanupBlocked">) {
    state = Object.freeze({ ...value, cleanupBlocked: blocked !== null });
    for (const listener of [...listeners]) {
      try { listener(); } catch (error) { notificationFailure ??= error; }
    }
  }
  function showBlocked() {
    publish({ kind: "error", hostEpoch: selected?.hostEpoch ?? null, sessionId: selected?.sessionId ?? null,
      snapshot: null, code: reload ? "stale_epoch" : "cleanup_failed" });
  }
  function detach(selection = selected) {
    if (selection !== selected) return;
    selected?.controller.abort();
    active?.selection.controller.abort();
    selected = desired = null;
    if (blocked) showBlocked();
    else publish({ kind: "idle", hostEpoch: null, sessionId: null, snapshot: null, code: null });
  }
  async function observe(owner: Owner) {
    const { hostEpoch, sessionId, controller, observeIdentity } = owner.selection;
    const isCurrent = () => selected === owner.selection && !controller.signal.aborted;
    const fail = (code: string) => {
      if (isCurrent() && state.code !== "stale_epoch") publish({ kind: "error", hostEpoch, sessionId, snapshot: null, code });
    };
    let revision: bigint | null = null;
    let projectionEpoch: string | null = null;
    try {
      owner.opening = open({ expectedHostEpoch: hostEpoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8_000 });
      owner.stream = await owner.opening;
      owner.iterator = owner.stream[Symbol.asyncIterator](); // Even a late acquisition must be returned.
      while (isCurrent()) {
        owner.next = owner.iterator.next();
        const result = await owner.next;
        if (result.done) { fail("ended_without_close"); return; }
        const item = result.value;
        // Identity is authority evidence even after detach; malformed or unrelated envelopes are not.
        if (!correlatedIdentity(item, sessionId)) { fail("invalid_update"); return; }
        if (item.status === "stale_epoch" || item.hostEpoch !== hostEpoch) {
          reload = true; desired = null;
          try { observeIdentity?.({ status: "stale_epoch", epoch: item.hostEpoch }); }
          catch (error) { notificationFailure ??= error; }
          if (selected) publish({ kind: "error", hostEpoch: selected.hostEpoch, sessionId: selected.sessionId, snapshot: null, code: "stale_epoch" });
          return;
        }
        if (!isCurrent()) return;
        if (item.status !== "ok") {
          fail(item.status);
          return;
        }
        const next = decimalRevision(item.revision);
        if (next === null || (revision === null && (!item.isInitial || item.previousRevision !== null))) {
          fail("invalid_update"); return;
        }
        if (projectionEpoch !== null && projectionEpoch !== item.projectionEpoch) { fail("stale_projection"); return; }
        if (revision !== null && next <= revision) continue; // Never apply duplicate or out-of-order callbacks.
        const previous = decimalRevision(item.previousRevision);
        if (revision !== null && (item.isInitial || previous === null || previous >= next)) { fail("invalid_update"); return; }
        if (item.session !== null && (!validToolActivities(item.session.toolActivities, item.session.evictedToolActivities)
          || !item.session.text.every(validRowOrder))) {
          fail("invalid_update"); return;
        }
        const gap = item.hasGap || (revision !== null && (next > revision + 1n || previous !== revision));
        revision = next;
        projectionEpoch = item.projectionEpoch;
        // Replace, never append/replay. A null session removes all old state; missing rows disappear.
        const snapshot = immutableReplacement({ ...item, hasGap: gap });
        publish({ kind: item.isClosed ? "closed" : "connected", hostEpoch, sessionId, snapshot, code: null });
        if (item.isClosed) return;
      }
    } catch (error) {
      owner.failure = error;
      fail("transport_failed"); // Never display exception messages or arbitrary transport error text.
    } finally {
      try {
        if (owner.stream) {
          if (!owner.iterator?.return) throw new Error("Iterator cleanup unavailable");
          owner.returning = owner.iterator.return();
          if (!(await owner.returning).done) throw new Error("Iterator cleanup incomplete");
        }
      } catch (error) {
        blocked = { owner, error }; desired = null;
        showBlocked(); // Cleanup failure is not fenced by the obsolete presentation generation.
      }
    }
  }
  function start() {
    if (running || blocked || reload) return;
    running = true;
    work = Promise.resolve().then(async () => {
      try {
        while (desired && !blocked && !reload) {
          const selection = desired; desired = null;
          active = { selection };
          await observe(active);
          if (!blocked) active = null;
        }
      } finally { running = false; }
    });
  }
  function select(hostEpoch: string, sessionId: string, observeIdentity?: Selection["observeIdentity"]) {
    selected?.controller.abort(); active?.selection.controller.abort();
    const selection: Selection = { hostEpoch, sessionId, controller: new AbortController(), observeIdentity };
    selected = selection;
    if (blocked) showBlocked();
    else if (reload) publish({ kind: "error", hostEpoch, sessionId, snapshot: null, code: "stale_epoch" });
    else {
      desired = selection; // Exactly one replaceable desired selection, not a chain of waiters.
      publish({ kind: "loading", hostEpoch, sessionId, snapshot: null, code: null });
      start();
    }
    return { detach: () => detach(selection) };
  }
  return {
    getSnapshot: () => state,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    select,
    detach,
    retainedCleanupFailure: () => blocked,
    notificationFailure: () => notificationFailure,
    // Observer settlement is NOT successful cleanup: inspect retainedCleanupFailure as well.
    // Neither this promise nor generated cancellation/timeout proves backend termination.
    settled: () => work,
  };
}

function decimalRevision(value: string | null): bigint | null {
  if (typeof value !== "string") return null;
  const match = /^(0|[1-9][0-9]{0,18})$/.exec(value);
  return match && match[0] === value && BigInt(value) <= 9223372036854775807n ? BigInt(value) : null;
}

function identity(value: unknown): value is string {
  return validToolIdentity(value) && !/^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]$/.test(value);
}

function correlatedIdentity(item: SessionDisplayItem, sessionId: string): boolean {
  if (!item || !["ok", "invalid_request", "stale_epoch", "capacity", "observation_failed"].includes(item.status) ||
    !identity(item.hostEpoch) || !identity(item.sessionId) || item.sessionId !== sessionId) return false;
  // Error envelopes legitimately omit projection/revisions/session, but malformed supplied values
  // (including obsolete ones) are never host-replacement proof. Legacy opaque epoch strings remain valid.
  if ((item.projectionEpoch !== null && !identity(item.projectionEpoch)) ||
    (item.revision !== null && decimalRevision(item.revision) === null) ||
    (item.previousRevision !== null && decimalRevision(item.previousRevision) === null) ||
    (item.session !== null && (!identity(item.session?.sessionId) ||
      item.session.sessionId.toLowerCase() !== sessionId.toLowerCase() || decimalRevision(item.session.revision) === null))) return false;
  return item.status !== "ok" || (identity(item.projectionEpoch) && decimalRevision(item.revision) !== null);
}

function wellFormedToolString(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const unit = value.charCodeAt(index);
    if (unit < 0xd800 || unit > 0xdfff) continue;
    if (unit > 0xdbff || ++index === value.length) return false;
    const low = value.charCodeAt(index);
    if (low < 0xdc00 || low > 0xdfff) return false;
  }
  return true;
}

function validToolIdentity(value: unknown): value is string {
  // Match .NET IsNullOrWhiteSpace, not JS trim (which disagrees on U+0085 and U+FEFF).
  return typeof value === "string" && value.length > 0 && value.length <= 256 &&
    !/^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]*$/.test(value) && wellFormedToolString(value);
}

function validToolActivities(rows: unknown, evicted: unknown): boolean {
  if (!Array.isArray(rows) || rows.length > 2 || typeof evicted !== "string") return false;
  // Require a canonical nonnegative Int64 STRING, including an exact end (JS $ also matches before a final newline).
  const decimal = /^(0|[1-9][0-9]{0,18})$/.exec(evicted);
  if (!decimal || decimal[0] !== evicted || BigInt(evicted) > 9223372036854775807n) return false;
  const identities = new Set<string>();
  for (const row of rows) {
    if (row === null || typeof row !== "object" || Array.isArray(row) ||
      !validToolIdentity(row.providerId) || (row.runId !== null && !validToolIdentity(row.runId)) || !validToolIdentity(row.activityId) ||
      !["Requested", "Started", "Progressed", "Completed", "Failed", "Canceled"].includes(row.phase) ||
      typeof row.isNameTruncated !== "boolean" || !validRowOrder(row) ||
      (row.name === null ? row.isNameTruncated : typeof row.name !== "string" || row.name.length > 128 || !wellFormedToolString(row.name))) return false;
    const identity = JSON.stringify([row.providerId, row.runId, row.activityId]);
    if (identities.has(identity)) return false;
    identities.add(identity);
  }
  return true;
}

function validRowOrder(row: { timestamp: string | null; sequence: string | null }): boolean {
  return (row.timestamp === null || typeof row.timestamp === "string" && Number.isFinite(Date.parse(row.timestamp)))
    && (row.sequence === null || decimalRevision(row.sequence) !== null);
}

function immutableReplacement(item: SessionDisplayItem): SessionDisplayItem {
  return Object.freeze({ ...item, session: item.session === null ? null : Object.freeze({
    ...item.session,
    lifecycle: item.session.lifecycle && Object.freeze({ ...item.session.lifecycle }),
    configuration: item.session.configuration && Object.freeze({ ...item.session.configuration }),
    text: Object.freeze(item.session.text.map(row => Object.freeze({ ...row }))),
    toolActivities: Object.freeze(item.session.toolActivities.map(row => Object.freeze({
      providerId: row.providerId, runId: row.runId, activityId: row.activityId, phase: row.phase,
      name: row.name, isNameTruncated: row.isNameTruncated,
      timestamp: row.timestamp, sequence: row.sequence,
    }))),
  }) });
}

export function displayRowKey(sessionId: string, row: SessionDisplayText): string {
  return JSON.stringify([sessionId, row.runId, row.contentId, row.kind]);
}

export function displayToolActivityKey(sessionId: string, row: SessionDisplayToolActivity): string {
  return JSON.stringify([sessionId, row.providerId, row.runId, row.activityId]);
}
