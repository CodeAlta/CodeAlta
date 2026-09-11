import type { SessionDisplayItem, SessionDisplayRequest, SessionDisplayText } from "#neoastra";

type OpenDisplay = (request: SessionDisplayRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<AsyncIterable<SessionDisplayItem>>;
export type DisplayState = Readonly<{
  kind: "idle" | "loading" | "connected" | "closed" | "error";
  hostEpoch: string | null;
  sessionId: string | null;
  snapshot: SessionDisplayItem | null;
  code: string | null;
}>;

// Instance-owned, selected-session-only renderer state. NeoAstra owns the actual channel/credits/buffering.
export function createSessionDisplayStore(open: OpenDisplay) {
  let state: DisplayState = Object.freeze({ kind: "idle", hostEpoch: null, sessionId: null, snapshot: null, code: null });
  const listeners = new Set<() => void>();
  let generation = 0;
  let active: AbortController | null = null;
  let work = Promise.resolve();
  function publish(value: DisplayState) {
    state = Object.freeze(value);
    for (const listener of listeners) listener();
  }
  function detach() {
    generation++;
    active?.abort();
    active = null;
    publish({ kind: "idle", hostEpoch: null, sessionId: null, snapshot: null, code: null });
  }
  function select(hostEpoch: string, sessionId: string) {
    const current = ++generation;
    active?.abort();
    const controller = new AbortController();
    active = controller;
    const isCurrent = () => generation === current && !controller.signal.aborted;
    publish({ kind: "loading", hostEpoch, sessionId, snapshot: null, code: null });
    const fail = (code: string) => {
      if (isCurrent() && state.code !== "stale_epoch") publish({ kind: "error", hostEpoch, sessionId, snapshot: null, code });
    };
    // Join local iterator cleanup before opening the next selected-session channel. Aborting uses
    // the normal generated API; it never issues a session abort or disposes the application host.
    work = work.then(async () => {
      if (!isCurrent()) return;
      let iterator: AsyncIterator<SessionDisplayItem> | undefined;
      let revision: bigint | null = null;
      let projectionEpoch: string | null = null;
      try {
        const stream = await open({ expectedHostEpoch: hostEpoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8_000 });
        iterator = stream[Symbol.asyncIterator]();
        while (isCurrent()) {
          const result = await iterator.next();
          if (!isCurrent()) return;
          if (result.done) { fail("ended_without_close"); return; }
          const item = result.value;
          if (item.status !== "ok") {
            fail(["invalid_request", "stale_epoch", "capacity", "observation_failed"].includes(item.status) ? item.status : "observation_failed");
            return;
          }
          if (item.hostEpoch !== hostEpoch) { fail("stale_epoch"); return; }
          if (item.sessionId !== sessionId || (item.session && item.session.sessionId.toLowerCase() !== sessionId.toLowerCase())) {
            fail("invalid_update"); return;
          }
          const next = decimalRevision(item.revision);
          if (next === null || !item.projectionEpoch || (revision === null && (!item.isInitial || item.previousRevision !== null))) {
            fail("invalid_update"); return;
          }
          if (projectionEpoch !== null && projectionEpoch !== item.projectionEpoch) { fail("stale_projection"); return; }
          if (revision !== null && next <= revision) continue; // Never apply duplicate or out-of-order callbacks.
          const previous = decimalRevision(item.previousRevision);
          if (revision !== null && (item.isInitial || previous === null || previous >= next)) { fail("invalid_update"); return; }
          const gap = item.hasGap || (revision !== null && (next > revision + 1n || previous !== revision));
          revision = next;
          projectionEpoch = item.projectionEpoch;
          // Replace, never append/replay. A null session removes all old state; missing rows disappear.
          const snapshot = immutableReplacement({ ...item, hasGap: gap });
          publish({ kind: item.isClosed ? "closed" : "connected", hostEpoch, sessionId, snapshot, code: null });
          if (item.isClosed) return;
        }
      } catch {
        fail("transport_failed"); // Never display exception messages or arbitrary transport error text.
      } finally {
        try { await iterator?.return?.(); }
        catch { fail("transport_failed"); }
      }
    });
  }
  return {
    getSnapshot: () => state,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    select,
    detach,
    // Useful to join cleanup in tests/outer renderer lifetime; no command or host work is joined here.
    settled: () => work,
  };
}

function decimalRevision(value: string | null): bigint | null {
  return value !== null && /^(0|[1-9][0-9]{0,18})$/.test(value) ? BigInt(value) : null;
}

function immutableReplacement(item: SessionDisplayItem): SessionDisplayItem {
  return Object.freeze({ ...item, session: item.session === null ? null : Object.freeze({
    ...item.session,
    lifecycle: item.session.lifecycle && Object.freeze({ ...item.session.lifecycle }),
    configuration: item.session.configuration && Object.freeze({ ...item.session.configuration }),
    text: Object.freeze(item.session.text.map(row => Object.freeze({ ...row }))),
  }) });
}

export function displayRowKey(sessionId: string, row: SessionDisplayText): string {
  return JSON.stringify([sessionId, row.runId, row.contentId, row.kind]);
}
